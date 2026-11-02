using MikePDF.Broker;

namespace MikePDF.Integration.Tests.Support;

/// <summary>
/// The real, freshly built engine in a private directory the AppContainer can read. Located through MIKEPDF_ENGINE_DIR (set by
/// build.ps1) or, failing that, the CMake output directory below the repository root.
/// </summary>
public sealed class SandboxFixture : IDisposable
{
    /// <summary>
    /// The start of every profile name this fixture makes. The profile is <b>per run</b> (process id and a random suffix follow): two test runs on one
    /// machine, such as the same suite in two git worktrees at once, used to share one profile named "MikePDF.IntegrationTests", and the first to finish
    /// deleted it under the other, whose engines then could not start.
    /// </summary>
    public const string ProfilePrefix = "MikePDF.IT";

    private static readonly string[] Configurations = ["Release", "Debug"];

    private static readonly string[] DeployedFiles = ["MikePDF.Engine.exe", "pdfium.dll", "MikePDF.Engine.SandboxProbe.exe", "rogue-copy.dll"];

    public SandboxFixture()
    {
        RepositoryRoot = FindRepositoryRoot();
        var source = Environment.GetEnvironmentVariable("MIKEPDF_ENGINE_DIR");
        if (string.IsNullOrEmpty(source) || !Directory.Exists(source))
        {
            source = Path.Combine(RepositoryRoot, "build", "out", "cmake-x64", "bin", "Release");
        }

        foreach (var file in DeployedFiles)
        {
            if (!File.Exists(Path.Combine(source, file)))
            {
                throw new FileNotFoundException($"{file} not found in {source}. Build the native half first: ./build.ps1 -Test", file);
            }
        }

        var parent = Path.Combine(RepositoryRoot, "build", "out", "integration");
        Directory.CreateDirectory(parent);
        TestProfiles.SweepStale(parent);

        var profileName = TestProfiles.NewName(ProfilePrefix);
        DeployDirectory = Path.Combine(parent, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(DeployDirectory);
        TestProfiles.WriteOwner(DeployDirectory, profileName); // before the profile exists: a run killed from here on is found and cleaned up by the next one
        Profile = AppContainerProfile.CreateOrOpen(profileName, "MikePDF integration tests", "Zero-capability AppContainer for the MikePDF integration tests");
        EngineDeployment.EnsureAppContainerAccess(DeployDirectory, Profile); // before the copy, so the files inherit it
        foreach (var file in DeployedFiles)
        {
            File.Copy(Path.Combine(source, file), Path.Combine(DeployDirectory, file));
        }

        // The WriterHost is self-contained: its own runtime sits beside it, so the AppContainer needs read access to this directory and nothing else.
        CopyDirectory(FindWriterHostDirectory(), WriterHostDirectory);
    }

    public AppContainerProfile Profile { get; }

    public string RepositoryRoot { get; }

    public string DeployDirectory { get; }

    public string WriterHostDirectory => Path.Combine(DeployDirectory, "writerhost");

    public string WriterHostPath => Path.Combine(WriterHostDirectory, "MikePDF.WriterHost.exe");

    public string EnginePath => Path.Combine(DeployDirectory, "MikePDF.Engine.exe");

    public string ProbePath => Path.Combine(DeployDirectory, "MikePDF.Engine.SandboxProbe.exe");

    public EngineSupervisorOptions Options(Func<EngineSupervisorOptions, EngineSupervisorOptions>? configure = null)
    {
        var options = new EngineSupervisorOptions { EnginePath = EnginePath, Profile = Profile, SlotCount = 8, SlotSize = 4 << 20 };
        return configure is null ? options : configure(options);
    }

    public EngineSupervisorOptions WriterHostOptions(Func<EngineSupervisorOptions, EngineSupervisorOptions>? configure = null)
    {
        var options = new EngineSupervisorOptions { EnginePath = WriterHostPath, Profile = Profile, Peer = MikePDF.Protocol.ProcessKind.WriterHost, Policy = new SandboxPolicy { ProhibitDynamicCode = false } };
        return configure is null ? options : configure(options);
    }

    public Task<WriterHostClient> StartWriterHostAsync(Func<EngineSupervisorOptions, EngineSupervisorOptions>? configure = null) =>
        WriterHostClient.StartAsync(WriterHostOptions(configure));

    public Task<EngineSupervisor> StartAsync(Func<EngineSupervisorOptions, EngineSupervisorOptions>? configure = null) =>
        EngineSupervisor.StartAsync(Options(configure));

    public static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    public void Dispose()
    {
        Cleanup(() => EngineDeployment.RevokeAppContainerAccess(DeployDirectory, Profile.Sid));
        Cleanup(() => Directory.Delete(DeployDirectory, recursive: true));
        Cleanup(Profile.Delete);
    }

    private static void Cleanup(Action action) => TestProfiles.TryDo(action);

    private string FindWriterHostDirectory()
    {
        // The newest self-contained build output of MikePDF.WriterHost (it is built before this test project runs).
        var found = Configurations
            .Select(configuration => Path.Combine(RepositoryRoot, "src", "MikePDF.WriterHost", "bin", configuration))
            .Where(Directory.Exists)
            .SelectMany(directory => Directory.GetFiles(directory, "MikePDF.WriterHost.exe", SearchOption.AllDirectories))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault() ?? throw new FileNotFoundException("MikePDF.WriterHost.exe not found; build the solution first: ./build.ps1 -Test");
        return Path.GetDirectoryName(found)!;
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.GetDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MikePDF.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("MikePDF.slnx not found above " + AppContext.BaseDirectory);
    }
}
