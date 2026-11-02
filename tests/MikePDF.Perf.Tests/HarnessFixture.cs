using MikePDF.Perf.Documents;
using MikePDF.Perf.Scenarios;
using MikePDF.Perf.Setup;

namespace MikePDF.Perf.Tests;

/// <summary>The real engine in a private directory and the four generated documents on disk, for the whole test run. Removed at the end.</summary>
public sealed class HarnessFixture : IDisposable
{
    /// <summary>Per run (the process id and a random suffix): a profile is a machine-wide object, and a name shared with another run, in another git worktree say, is deleted under it by whichever finishes first.</summary>
    public static readonly string ProfileName = $"MikePDF.PerfTests.{Environment.ProcessId}.{Guid.NewGuid().ToString("N")[..8]}";

    /// <summary>The profile the CLI under test is asked to use, per run for the same reason. Deleted when the fixture is.</summary>
    public static readonly string CliProfileName = ProfileName + ".Cli";

    private readonly string _documentsDirectory = Path.Combine(Path.GetTempPath(), "mikepdf-perf-docs-" + Guid.NewGuid().ToString("N")[..8]);

    public HarnessFixture()
    {
        Engine = EngineWorkspace.Create(engineDirectory: null, ProfileName);
        Documents = PerfDocuments.Materialize(_documentsDirectory, PerfDocuments.All).ToDictionary(d => d.Spec.Id, StringComparer.Ordinal);
    }

    internal EngineWorkspace Engine { get; }

    internal IReadOnlyDictionary<string, GeneratedDocument> Documents { get; }

    internal string DocumentsDirectory => _documentsDirectory;

    internal PerfContext Context(PerfProfile profile, TimeSpan? crashBackoff = null) =>
        new() { Engine = Engine, Documents = Documents, Profile = profile, CrashBackoff = crashBackoff ?? TimeSpan.FromMilliseconds(250) };

    public void Dispose()
    {
        Engine.Dispose();
        try
        {
            MikePDF.Broker.AppContainerProfile.Delete(CliProfileName);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Never created, or already gone.
        }

        try
        {
            Directory.Delete(_documentsDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Left for the OS temp cleanup.
        }
    }
}
