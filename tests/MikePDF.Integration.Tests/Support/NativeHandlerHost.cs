using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace MikePDF.Integration.Tests.Support;

/// <summary>
/// The native handler DLL as Explorer's hosts use it, minus Explorer: <c>mikepdf_shellext_testhost.exe</c> loads <c>MikePDF.ShellExt.dll</c> with LoadLibrary and
/// DllGetClassObject (nothing is registered anywhere), and the DLL starts the real ShellHost, which starts the real sandboxed engine. The environment variables
/// point all three at this test run's own pipe, engine directory, AppContainer profile and data folder; the DLL and ShellHost read them only when they have no package identity.
/// </summary>
internal sealed class NativeHandlerHost : IDisposable
{
    private readonly SandboxFixture _sandbox;

    public NativeHandlerHost(SandboxFixture sandbox, int idleSeconds = 30)
    {
        _sandbox = sandbox;
        var native = Environment.GetEnvironmentVariable("MIKEPDF_ENGINE_DIR");
        if (string.IsNullOrEmpty(native) || !Directory.Exists(native))
        {
            native = Path.Combine(sandbox.RepositoryRoot, "build", "out", "cmake-x64", "bin", "Release");
        }

        TestHostPath = Path.Combine(native, "mikepdf_shellext_testhost.exe");
        DllPath = Path.Combine(native, "MikePDF.ShellExt.dll");
        if (!File.Exists(TestHostPath) || !File.Exists(DllPath))
        {
            throw new FileNotFoundException("mikepdf_shellext_testhost.exe or MikePDF.ShellExt.dll not found in " + native + ". Build the native half first: ./build.ps1 -Test");
        }

        ShellHostPath = Directory.GetFiles(Path.Combine(sandbox.RepositoryRoot, "src", "MikePDF.ShellHost", "bin"), "MikePDF.ShellHost.exe", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault() ?? throw new FileNotFoundException("MikePDF.ShellHost.exe not found; build the solution first");
        PipeName = "mikepdf-test-handler-" + Guid.NewGuid().ToString("N")[..10];
        DataDirectory = Path.Combine(Path.GetTempPath(), "mikepdf-handler-data-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(DataDirectory, "private"));
        IdleSeconds = idleSeconds;
    }

    public string TestHostPath { get; }

    public string DllPath { get; }

    public string ShellHostPath { get; }

    public string PipeName { get; }

    public string DataDirectory { get; }

    public int IdleSeconds { get; }

    public string SettingsPath => Path.Combine(DataDirectory, "private", "settings.json");

    public void ApplyEnvironment(ProcessStartInfo start)
    {
        start.Environment["MIKEPDF_SHELL_TEST_PIPE"] = PipeName;
        start.Environment["MIKEPDF_SHELL_TEST_HOST"] = ShellHostPath;
        start.Environment["MIKEPDF_SHELL_TEST_ENGINE"] = _sandbox.DeployDirectory;
        start.Environment["MIKEPDF_SHELL_TEST_PROFILE"] = _sandbox.Profile.Name;
        start.Environment["MIKEPDF_SHELL_TEST_DATA"] = DataDirectory;
        start.Environment["MIKEPDF_SHELL_TEST_IDLE_SECONDS"] = IdleSeconds.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The user's settings file as the app writes it; ShellHost reads it on every request.</summary>
    public void WriteSettings(string valuesJson) => File.WriteAllText(SettingsPath, "{ \"schemaVersion\": 1, \"values\": { " + valuesJson + " } }");

    public sealed record ThumbResult(bool Ok, string Raw, uint Hresult, int Width, int Height, int NonWhitePercent, double Milliseconds, int Alpha, byte[] Bgra);

    /// <summary>One GetThumbnail in a new host process. The first call with no ShellHost running makes the DLL start it.</summary>
    public async Task<ThumbResult> ThumbAsync(string pdfPath, int cx, bool keepPixels = false)
    {
        var rawPath = keepPixels ? Path.Combine(DataDirectory, Guid.NewGuid().ToString("N") + ".raw") : null;
        var start = new ProcessStartInfo(TestHostPath) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("thumb");
        start.ArgumentList.Add(pdfPath);
        start.ArgumentList.Add(cx.ToString(CultureInfo.InvariantCulture));
        if (rawPath is not null)
        {
            start.ArgumentList.Add(rawPath);
        }

        ApplyEnvironment(start);
        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        var line = output.Trim().Split('\n').Last().Trim();
        var parts = line.Split(' ');
        if (parts[0] == "OK")
        {
            var bgra = rawPath is not null && File.Exists(rawPath) ? File.ReadAllBytes(rawPath) : [];
            return new ThumbResult(true, line, 0, int.Parse(parts[1], CultureInfo.InvariantCulture), int.Parse(parts[2], CultureInfo.InvariantCulture), int.Parse(parts[3], CultureInfo.InvariantCulture),
                double.Parse(parts[4], CultureInfo.InvariantCulture), int.Parse(parts[5], CultureInfo.InvariantCulture), bgra);
        }

        Assert.StartsWith("FAIL", line, StringComparison.Ordinal);
        return new ThumbResult(false, line, Convert.ToUInt32(parts[1], 16), 0, 0, 0, double.Parse(parts[2], CultureInfo.InvariantCulture), 0, []);
    }

    /// <summary>The process id of whatever serves the pipe now, or 0 when nothing does (asked of the pipe itself, as the handler asks).</summary>
    public uint ServerProcessId()
    {
        using var client = new System.IO.Pipes.NamedPipeClientStream(".", PipeName, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.None);
        try
        {
            client.Connect(300);
        }
        catch (Exception e) when (e is TimeoutException or IOException)
        {
            return 0;
        }

        return GetNamedPipeServerProcessId(client.SafePipeHandle.DangerousGetHandle(), out var pid) ? pid : 0;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(nint pipe, out uint pid);

    /// <summary>One preview, driven by commands on its standard input (see testhost.cpp).</summary>
    public async Task<PreviewSession> StartPreviewAsync()
    {
        var start = new ProcessStartInfo(TestHostPath) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("preview");
        ApplyEnvironment(start);
        var process = Process.Start(start)!;
        var session = new PreviewSession(process);
        Assert.Equal("READY", await session.ReadLineAsync());
        return session;
    }

    public void Dispose()
    {
        try
        {
            if (ServerProcessId() is var pid and not 0)
            {
                using var shellHost = Process.GetProcessById((int)pid);
                shellHost.Kill();
            }
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }

        try
        {
            Directory.Delete(DataDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    public sealed class PreviewSession(Process process) : IAsyncDisposable
    {
        public async Task<string> ReadLineAsync() => await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(60)) ?? "(closed)";

        /// <summary>Sends one command and returns its one-line answer.</summary>
        public async Task<string> CommandAsync(string command)
        {
            await process.StandardInput.WriteLineAsync(command);
            await process.StandardInput.FlushAsync();
            return await ReadLineAsync();
        }

        /// <summary>The value of the preview window as UI Automation reports it, for example "Page 3 of 200".</summary>
        public async Task<(string Name, string Value)> StateAsync()
        {
            var line = await CommandAsync("state");
            Assert.StartsWith("STATE 1", line, StringComparison.Ordinal);
            var name = line[(line.IndexOf("name=", StringComparison.Ordinal) + 5)..line.IndexOf(" value=", StringComparison.Ordinal)];
            return (name, line[(line.IndexOf(" value=", StringComparison.Ordinal) + 7)..]);
        }

        public async Task<bool> UntilValueAsync(string expected, int milliseconds = 8000)
        {
            for (var waited = 0; waited < milliseconds; waited += 50)
            {
                await CommandAsync("settle 50");
                if ((await StateAsync()).Value == expected)
                {
                    return true;
                }
            }

            return false;
        }

        public async Task<int> GdiObjectsAsync() => int.Parse((await CommandAsync("gdi"))["GDI ".Length..], CultureInfo.InvariantCulture);

        public async ValueTask DisposeAsync()
        {
            try
            {
                await process.StandardInput.WriteLineAsync("quit");
                await process.StandardInput.FlushAsync();
            }
            catch (IOException)
            {
            }

            if (!process.WaitForExit(5000))
            {
                process.Kill();
            }

            process.Dispose();
        }
    }

    /// <summary>A document of <paramref name="pages"/> US Letter pages, each with its number drawn in large black text.</summary>
    public static byte[] ManyPages(int pages)
    {
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();

        void Add(string body)
        {
            offsets.Add(pdf.Length);
            pdf.Append(offsets.Count).Append(" 0 obj\n").Append(body).Append("\nendobj\n");
        }

        Add("<< /Type /Catalog /Pages 2 0 R >>");
        var kids = string.Join(' ', Enumerable.Range(0, pages).Select(i => (4 + 2 * i).ToString(CultureInfo.InvariantCulture) + " 0 R"));
        Add($"<< /Type /Pages /Kids [{kids}] /Count {pages} /MediaBox [0 0 612 792] >>");
        Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        for (var i = 0; i < pages; i++)
        {
            Add($"<< /Type /Page /Parent 2 0 R /Resources << /Font << /F1 3 0 R >> >> /Contents {5 + 2 * i} 0 R >>");
            var content = $"BT /F1 96 Tf 100 600 Td (Page {i + 1}) Tj ET 0 0 0 rg 100 100 400 40 re f";
            Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        }

        var xref = pdf.Length;
        pdf.Append("xref\n0 ").Append(offsets.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            pdf.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }

        pdf.Append("trailer\n<< /Size ").Append(offsets.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(pdf.ToString());
    }
}
