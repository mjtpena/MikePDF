using System.Diagnostics;

namespace MikePDF.UI.Tests.Pages;

/// <summary>
/// A harmless long-running process whose executable is called MikePDF.Engine.exe (a renamed copy of ping), standing in for an engine that is
/// not the app under test's: another agent's integration test, the render tool, a person's own reader. A journey that kills engines by name would
/// kill it; one that kills only the app's own does not.
/// </summary>
internal sealed class DecoyEngine : IDisposable
{
    private readonly Process _process;

    private DecoyEngine(Process process) => _process = process;

    public int Id => _process.Id;

    public bool HasExited => _process.HasExited;

    public static DecoyEngine Start(string directory)
    {
        var folder = Path.Combine(directory, "decoy-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "MikePDF.Engine.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), path);
        var start = new ProcessStartInfo(path, "-n 300 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        return new DecoyEngine(Process.Start(start)!);
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill();
            }
        }
        catch (InvalidOperationException)
        {
        }

        _process.Dispose();
    }
}
