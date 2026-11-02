using System.IO.Compression;
using System.Text.RegularExpressions;
using MikePDF.Perf.Metrics;
using MikePDF.Perf.Results;
using MikePDF.Perf.Setup;

namespace MikePDF.Perf.Scenarios;

/// <summary>
/// Install size (budget 60 MB per architecture), read from a built single-architecture MSIX. An MSIX is a zip: the unpacked size is the sum of the
/// lengths of its entries, which is what the install occupies, and the file size is what is downloaded. With no package the metrics are skipped, and
/// the reason says how to produce one. Nothing is built, installed or launched here.
/// </summary>
internal static partial class PackageScenario
{
    private const double Megabyte = 1024.0 * 1024.0;

    public static IReadOnlyList<MetricResult> Run(PerfContext context)
    {
        var path = Locate(context.PackagePath, context.Engine.RepositoryRoot);
        if (path is null)
        {
            const string reason = "No built MSIX was found (looked for build/out/package/MikePDF_<version>_<arch>.msix). Build one with build/package.ps1 -Arch <arch>, or pass --package <file>.";
            return [MetricResults.Skipped(MetricCatalog.InstallSize, reason), MetricResults.Skipped(MetricCatalog.MsixFileSize, reason)];
        }

        try
        {
            var (fileBytes, unpackedBytes, entries) = Measure(path);
            context.Log($"{MetricCatalog.InstallSize.Id}: {unpackedBytes / Megabyte:F1} MB unpacked, {fileBytes / Megabyte:F1} MB file ({Path.GetFileName(path)})");
            var details = new Dictionary<string, double>(StringComparer.Ordinal) { ["entries"] = entries };
            return
            [
                MetricResults.Measured(MetricCatalog.InstallSize, [unpackedBytes / Megabyte], details),
                MetricResults.Measured(MetricCatalog.MsixFileSize, [fileBytes / Megabyte]),
            ];
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            var reason = $"{Path.GetFileName(path)} could not be read as an MSIX: {e.Message}";
            return [MetricResults.Failed(MetricCatalog.InstallSize, reason), MetricResults.Failed(MetricCatalog.MsixFileSize, reason)];
        }
    }

    public static (long FileBytes, long UnpackedBytes, int Entries) Measure(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        return (new FileInfo(path).Length, archive.Entries.Sum(e => e.Length), archive.Entries.Count);
    }

    public static string? Locate(string? explicitPath, string repositoryRoot)
    {
        if (!string.IsNullOrEmpty(explicitPath))
        {
            return File.Exists(explicitPath) ? Path.GetFullPath(explicitPath) : null;
        }

        var directory = Path.Combine(repositoryRoot, "build", "out", "package");
        if (string.IsNullOrEmpty(repositoryRoot) || !Directory.Exists(directory))
        {
            return null;
        }

        return Directory.EnumerateFiles(directory, "MikePDF_*.msix")
            .Where(f => ReleasePackage().IsMatch(Path.GetFileName(f)) && Path.GetFileNameWithoutExtension(f).EndsWith("_" + EngineWorkspace.HostArchitecture, StringComparison.Ordinal))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    [GeneratedRegex(@"^MikePDF_[0-9.]+_(x64|arm64)\.msix$")]
    private static partial Regex ReleasePackage();
}
