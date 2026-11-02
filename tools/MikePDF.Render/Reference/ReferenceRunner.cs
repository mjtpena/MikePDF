using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using MikePDF.Render.Imaging;

namespace MikePDF.Render.Reference;

/// <summary>What one page of the reference run came to.</summary>
internal sealed record ReferencePage(string Status, int Width, int Height, long Ms, string Detail, string? PixelsPath);

/// <summary>The reference renderer's answer for one file.</summary>
internal sealed class ReferenceResult : IDisposable
{
    private readonly string _directory;

    public ReferenceResult(string directory) => _directory = directory;

    /// <summary><c>Ok</c> (the process finished), <c>Crashed</c>, <c>TimedOut</c> or <c>Failed</c> (could not run).</summary>
    public string Outcome { get; set; } = "Ok";

    /// <summary>What the reference made of opening the file (a protocol status name), or null if it never said.</summary>
    public string? OpenStatus { get; set; }

    public int PageCount { get; set; }

    public string? Detail { get; set; }

    public Dictionary<int, ReferencePage> Pages { get; } = [];

    public BgraImage? Load(int job)
    {
        if (!Pages.TryGetValue(job, out var page) || page.PixelsPath is null || !File.Exists(page.PixelsPath))
        {
            return null;
        }

        var data = File.ReadAllBytes(page.PixelsPath);
        if (data.Length < 12 || !data.AsSpan(0, 4).SequenceEqual("MBGR"u8))
        {
            return null;
        }

        var width = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
        var height = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8));
        if (width < 1 || height < 1 || (long)width * height * 4 != data.Length - 12)
        {
            return null;
        }

        return new BgraImage(width, height, data.AsSpan(12).ToArray());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Left in the work directory; the next run's sweep removes it.
        }
    }
}

/// <summary>Runs <c>mikepdf-refrender</c> (the pinned pdfium.dll, outside the sandbox) in a child process, so a file that crashes PDFium there cannot take this tool down.</summary>
internal sealed class ReferenceRunner(string executable, string pdfiumDll, string workDirectory)
{
    public async Task<ReferenceResult> RenderAsync(string pdfPath, string? password, int rotation, IReadOnlyList<(int Page, float Scale)> jobs, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(workDirectory, "ref-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        var result = new ReferenceResult(directory);
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "--pdfium", pdfiumDll, "--pdf", pdfPath, "--out", directory, "--rotation", rotation.ToString(CultureInfo.InvariantCulture) })
        {
            start.ArgumentList.Add(argument);
        }

        start.ArgumentList.Add("--jobs");
        start.ArgumentList.Add(string.Join(',', jobs.Select(j => FormattableString.Invariant($"{j.Page}:{j.Scale:R}"))));
        if (!string.IsNullOrEmpty(password))
        {
            start.ArgumentList.Add("--password");
            start.ArgumentList.Add(password);
        }

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            result.Outcome = "Failed";
            result.Detail = "Could not start the reference renderer: " + e.Message;
            return result;
        }

        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            while (await process.StandardOutput.ReadLineAsync(limit.Token).ConfigureAwait(false) is { } line)
            {
                Parse(line, directory, result);
            }

            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                result.Outcome = process.ExitCode == 2 ? "Failed" : "Crashed";
                result.Detail = $"Exit code 0x{process.ExitCode:X}. {Trim(await error.ConfigureAwait(false))}".Trim();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Kill(process);
            result.Outcome = "TimedOut";
            result.Detail = $"No result within {timeout.TotalSeconds:0} s; the reference renderer was killed.";
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }

        return result;
    }

    private static void Parse(string line, string directory, ReferenceResult result)
    {
        var parts = line.Split('\t');
        if (parts is ["DOC", var status, var count, ..])
        {
            result.OpenStatus = status;
            result.PageCount = int.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out var pages) ? pages : 0;
            result.Detail = parts.Length > 3 && parts[3].Length > 0 ? parts[3] : null;
        }
        else if (parts is ["PAGE", _, var job, var pageStatus, var width, var height, var ms, ..]
            && int.TryParse(job, NumberStyles.None, CultureInfo.InvariantCulture, out var jobIndex))
        {
            _ = int.TryParse(width, NumberStyles.None, CultureInfo.InvariantCulture, out var w);
            _ = int.TryParse(height, NumberStyles.None, CultureInfo.InvariantCulture, out var h);
            _ = long.TryParse(ms, NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds);
            result.Pages[jobIndex] = new ReferencePage(
                pageStatus, w, h, milliseconds, parts.Length > 7 ? parts[7] : "",
                pageStatus is "Ok" or "PartiallyRendered" ? Path.Combine(directory, FormattableString.Invariant($"job{jobIndex:D5}.bgra")) : null);
        }
    }

    private static string Trim(string text) => text.Length > 300 ? text[..300] : text.Trim();

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }
}
