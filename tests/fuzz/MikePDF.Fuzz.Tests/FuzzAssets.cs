using System.Reflection;

namespace MikePDF.Fuzz.Tests;

/// <summary>Where the seeds, regressions and dictionaries land next to the test assembly.</summary>
internal static class FuzzAssets
{
    public static string Directory(params string[] parts) => Path.Combine([AppContext.BaseDirectory, .. parts]);

    public static IReadOnlyList<string> Files(string directory, string pattern = "*") =>
        System.IO.Directory.Exists(directory)
            ? System.IO.Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList()
            : [];

    public static IReadOnlyList<string> GoldenFrames => Files(Directory("seeds", "golden"), "*.bin");

    public static IReadOnlyList<string> Pdfs => [.. Files(Directory("seeds", "pdf"), "*.pdf"), .. Files(Directory("seeds", "pdf-integration"), "*.pdf")];

    /// <summary>The apphost of the Protocol fuzz harness, as the build placed it.</summary>
    public static string ProtocolFuzzExe =>
        typeof(FuzzAssets).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "ProtocolFuzzExe").Value!;

    /// <summary>A scratch directory that is removed afterwards.</summary>
    public sealed class Scratch : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mikepdf-fuzz-test-" + Guid.NewGuid().ToString("N"));

        public Scratch() => System.IO.Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { System.IO.Directory.Delete(Path, recursive: true); }
            catch (IOException) { /* best effort */ }
            catch (UnauthorizedAccessException) { /* best effort */ }
        }
    }
}
