using MikePDF.Perf.Documents;
using MikePDF.Perf.Scenarios;
using MikePDF.Perf.Setup;

namespace MikePDF.Perf.Tests;

/// <summary>The real engine in a private directory and the four generated documents on disk, for the whole test run. Removed at the end.</summary>
public sealed class HarnessFixture : IDisposable
{
    public const string ProfileName = "MikePDF.PerfTests";

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
            Directory.Delete(_documentsDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Left for the OS temp cleanup.
        }
    }
}
