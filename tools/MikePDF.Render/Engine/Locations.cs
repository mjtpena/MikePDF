using System.Runtime.InteropServices;

namespace MikePDF.Render.Engine;

/// <summary>Where the pieces live: the repository, the built engine, the pinned PDFium, the corpus.</summary>
internal static class Locations
{
    public static string Architecture => RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";

    /// <summary>The folder with <c>MikePDF.slnx</c> above the current directory or the tool, or null outside a checkout.</summary>
    public static string? RepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "MikePDF.slnx")))
                {
                    return directory.FullName;
                }
            }
        }

        return null;
    }

    /// <summary>The folder holding <c>MikePDF.Engine.exe</c> and <c>pdfium.dll</c>.</summary>
    public static string EngineDirectory(string? explicitDirectory)
    {
        var candidates = new List<string?> { explicitDirectory, Environment.GetEnvironmentVariable("MIKEPDF_ENGINE_DIR") };
        if (RepositoryRoot() is { } root)
        {
            candidates.Add(Path.Combine(root, "build", "out", $"cmake-{Architecture}", "bin", "Release"));
            candidates.Add(Path.Combine(root, "build", "out", $"cmake-{Architecture}", "bin", "Debug"));
        }

        foreach (var candidate in candidates.Where(c => !string.IsNullOrEmpty(c)))
        {
            if (File.Exists(Path.Combine(candidate!, "MikePDF.Engine.exe")) && File.Exists(Path.Combine(candidate!, "pdfium.dll")))
            {
                return Path.GetFullPath(candidate!);
            }
        }

        throw new FileNotFoundException("MikePDF.Engine.exe and pdfium.dll were not found. Build the native half first (./build.ps1), or pass --engine-dir.");
    }

    /// <summary>The <c>pdfium.dll</c> the reference renderer loads: the pinned build, which is also the one beside the engine.</summary>
    public static string PdfiumDll(string? explicitDirectory, string engineDirectory)
    {
        var candidates = new List<string?> { explicitDirectory is null ? null : Path.Combine(explicitDirectory, "pdfium.dll") };
        if (RepositoryRoot() is { } root)
        {
            candidates.Add(Path.Combine(root, "build", "out", "pdfium", Architecture, "bin", "pdfium.dll"));
        }

        candidates.Add(Path.Combine(engineDirectory, "pdfium.dll"));
        return candidates.Where(c => c is not null && File.Exists(c)).Select(c => Path.GetFullPath(c!)).FirstOrDefault()
            ?? throw new FileNotFoundException("pdfium.dll was not found for the reference renderer. Pass --pdfium-dir or --no-reference.");
    }

    /// <summary>The reference renderer, built beside this tool.</summary>
    public static string ReferenceExecutable()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "mikepdf-refrender.exe");
        if (File.Exists(beside))
        {
            return beside;
        }

        if (RepositoryRoot() is { } root)
        {
            var project = Path.Combine(root, "tools", "MikePDF.Render.Reference", "bin");
            if (Directory.Exists(project))
            {
                var built = Directory.GetFiles(project, "mikepdf-refrender.exe", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                if (built is not null)
                {
                    return built;
                }
            }
        }

        throw new FileNotFoundException("mikepdf-refrender.exe was not found beside the tool. Build tools/MikePDF.Render.Reference, or pass --no-reference.");
    }

    public static string DefaultManifest() => Path.Combine(RepositoryRoot() ?? Directory.GetCurrentDirectory(), "tests", "corpus", "manifest.json");

    public static string DefaultCorpusDirectory() => Path.Combine(RepositoryRoot() ?? Directory.GetCurrentDirectory(), "build", "out", "corpus");

    /// <summary>The checked-in baseline for a tier on this architecture, if there is one.</summary>
    public static string? DefaultBaseline(string tier)
    {
        var path = Path.Combine(RepositoryRoot() ?? Directory.GetCurrentDirectory(), "tests", "corpus", "baselines", $"{tier}-{Architecture}.json");
        return File.Exists(path) ? path : null;
    }

    public static string DefaultWorkDirectory() =>
        RepositoryRoot() is { } root ? Path.Combine(root, "build", "out", "render") : Path.Combine(Path.GetTempPath(), "mikepdf-render");
}
