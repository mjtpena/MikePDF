using System.Runtime.InteropServices;
using System.Security.Cryptography;
using MikePDF.Broker;
using MikePDF.Perf.Results;
using MikePDF.Protocol;

namespace MikePDF.Perf.Setup;

/// <summary>
/// The real, freshly built engine in a private directory the AppContainer can read, and the AppContainer profile to run it in: the same arrangement
/// the integration tests use, so a measurement here is a measurement of the sandboxed engine and not of a convenient stand-in. The engine is located
/// through an explicit directory, MIKEPDF_ENGINE_DIR (set by build.ps1), or the CMake output below the repository root.
/// </summary>
public sealed class EngineWorkspace : IDisposable
{
    private static readonly string[] DeployedFiles = ["MikePDF.Engine.exe", "pdfium.dll"];

    private EngineWorkspace(string repositoryRoot, string deployDirectory, AppContainerProfile profile, EngineInfo info)
    {
        RepositoryRoot = repositoryRoot;
        DeployDirectory = deployDirectory;
        Profile = profile;
        Info = info;
    }

    /// <summary>The repository root when the tool runs from a checkout; empty otherwise.</summary>
    public string RepositoryRoot { get; }

    public string DeployDirectory { get; }

    public AppContainerProfile Profile { get; }

    public EngineInfo Info { get; }

    public string EnginePath => Path.Combine(DeployDirectory, "MikePDF.Engine.exe");

    /// <summary>The architecture directory name used by the build (cmake-x64, cmake-arm64) for this process.</summary>
    public static string HostArchitecture => RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";

    public static string? FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MikePDF.slnx")))
            {
                return directory.FullName;
            }
        }

        return null;
    }

    public static string LocateEngineDirectory(string? explicitDirectory, string? repositoryRoot)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(explicitDirectory))
        {
            candidates.Add(explicitDirectory);
        }

        var fromEnvironment = System.Environment.GetEnvironmentVariable("MIKEPDF_ENGINE_DIR");
        if (!string.IsNullOrEmpty(fromEnvironment))
        {
            candidates.Add(fromEnvironment);
        }

        if (repositoryRoot is not null)
        {
            foreach (var configuration in new[] { "Release", "Debug" })
            {
                candidates.Add(Path.Combine(repositoryRoot, "build", "out", "cmake-" + HostArchitecture, "bin", configuration));
            }
        }

        foreach (var candidate in candidates)
        {
            if (DeployedFiles.All(f => File.Exists(Path.Combine(candidate, f))))
            {
                return Path.GetFullPath(candidate);
            }
        }

        throw new FileNotFoundException($"MikePDF.Engine.exe and pdfium.dll not found (looked in: {string.Join("; ", candidates)}). Build the native half first: ./build.ps1 -SkipManaged", "MikePDF.Engine.exe");
    }

    /// <param name="engineDirectory">Where the built engine is; null to search.</param>
    /// <param name="profileName">The AppContainer profile to create or reuse. Distinct callers use distinct names so they can run side by side.</param>
    public static EngineWorkspace Create(string? engineDirectory, string profileName)
    {
        var root = FindRepositoryRoot();
        var source = LocateEngineDirectory(engineDirectory, root);
        var parent = root is null ? Path.Combine(Path.GetTempPath(), "MikePDF.Perf") : Path.Combine(root, "build", "out", "perf");
        Directory.CreateDirectory(parent);
        SweepStale(parent);

        var profile = AppContainerProfile.CreateOrOpen(profileName, "MikePDF performance harness", "Zero-capability AppContainer for the MikePDF performance harness");
        var deploy = Path.Combine(parent, "d-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(deploy);
        EngineDeployment.EnsureAppContainerAccess(deploy, profile); // before the copy, so the files inherit it
        foreach (var file in DeployedFiles)
        {
            File.Copy(Path.Combine(source, file), Path.Combine(deploy, file));
        }

        var info = new EngineInfo(Hash(Path.Combine(deploy, "MikePDF.Engine.exe")), Hash(Path.Combine(deploy, "pdfium.dll")), source);
        return new EngineWorkspace(root ?? string.Empty, deploy, profile, info);
    }

    /// <summary>The options the app uses for a document's own engine (slots of 1 MiB, six of them).</summary>
    public EngineSupervisorOptions DocumentOptions() => Options(EngineKind.Document, slotCount: 6);

    /// <summary>The options the app uses for the shared pool engine (placeholders, thumbnails, text and search).</summary>
    public EngineSupervisorOptions PoolOptions() => Options(EngineKind.Pool, slotCount: 4);

    public Task<EngineSupervisor> StartDocumentEngineAsync(CancellationToken cancellationToken = default) => EngineSupervisor.StartAsync(DocumentOptions(), cancellationToken);

    public Task<EngineSupervisor> StartPoolEngineAsync(CancellationToken cancellationToken = default) => EngineSupervisor.StartAsync(PoolOptions(), cancellationToken);

    public void Dispose()
    {
        Cleanup(() => EngineDeployment.RevokeAppContainerAccess(DeployDirectory, Profile.Sid));
        Cleanup(() => Directory.Delete(DeployDirectory, recursive: true));
        Cleanup(Profile.Delete);
    }

    private EngineSupervisorOptions Options(EngineKind kind, int slotCount) => new()
    {
        EnginePath = EnginePath,
        Profile = Profile,
        Kind = kind,
        Peer = ProcessKind.Engine,
        SlotCount = slotCount,
        SlotSize = 1 << 20,
    };

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void SweepStale(string parent)
    {
        foreach (var directory in Directory.GetDirectories(parent, "d-*"))
        {
            if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(directory) > TimeSpan.FromHours(1))
            {
                Cleanup(() => Directory.Delete(directory, recursive: true));
            }
        }
    }

    private static void Cleanup(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Best effort: a leftover is reclaimed by a later run's sweep and by CreateOrOpen.
        }
    }
}
