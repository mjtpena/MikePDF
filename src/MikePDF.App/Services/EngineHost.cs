using MikePDF.Broker;
using MikePDF.Protocol;

namespace MikePDF.App.Services;

/// <summary>
/// Everything needed to start sandboxed engines: the AppContainer profile and the engine's path (ADR-0004 and amendment A2).
/// The engine runs <b>in place</b>, from the package's own directory, which Windows protects and signs: nothing is copied, so there is no
/// unsigned executable in a user-writable folder and no gap between a check and a launch.
/// There is no fallback to a weaker sandbox: if the profile cannot be created or the engine is missing, documents do not open.
/// </summary>
internal sealed class EngineHost
{
    private readonly AppContainerProfile _profile;
    private readonly string _enginePath;

    private EngineHost(AppContainerProfile profile, string enginePath, int maxDocumentEngines, int engineMemoryLimitMb)
    {
        _profile = profile;
        _enginePath = enginePath;
        EngineMemoryLimitMb = engineMemoryLimitMb;
        Budget = new EngineBudget(new EngineBudgetOptions
        {
            Document = DocumentOptions(),
            Pool = PoolOptions(),
            MaxDocumentEngines = Math.Max(1, maxDocumentEngines),
            EngineMemoryLimitMb = engineMemoryLimitMb,
        });
    }

    /// <summary>The memory limit every engine process runs under, in megabytes: the administrator's <c>EngineMemoryLimitMB</c> policy (2048 when none is set).</summary>
    public int EngineMemoryLimitMb { get; }

    /// <summary>
    /// The engines the app may have at once (ADR-0004 amendment A3): one shared pool engine and at most <c>engines.maxLiveTabs</c> document engines (6 by default, set by policy), the least
    /// recently used suspended over the cap. Every <see cref="DocumentSession"/>, in every window, takes its engines from here.
    /// </summary>
    public EngineBudget Budget { get; }

    /// <param name="maxDocumentEngines">The cap on document engines at start-up (<c>engines.maxLiveTabs</c>, after policy); the app changes it later with <see cref="EngineBudget.SetMaxDocumentEngines"/>.</param>
    /// <param name="engineMemoryLimitMb">The administrator's <c>EngineMemoryLimitMB</c> policy: the job limit of every engine process the host starts, document engines and the pool engine alike. Null reads the policy from the registry.</param>
    public static EngineHost Create(int maxDocumentEngines = 6, int? engineMemoryLimitMb = null)
    {
        var memoryLimitMb = engineMemoryLimitMb ?? AppServices.OpenPolicy().Current.EngineMemoryLimitMb;
        if (!AppPaths.IsPackaged)
        {
            // Without package identity the child gets no access to the engine's directory (nothing grants it): development builds are run as a registered layout.
            throw new EngineStartException("MikePDF runs as an installed package; register a build with Add-AppxPackage -Register, or install a package from build/package.ps1.");
        }

        var enginePath = Path.Combine(AppPaths.EngineSource, "MikePDF.Engine.exe");
        if (!File.Exists(enginePath) || !File.Exists(Path.Combine(AppPaths.EngineSource, "pdfium.dll")))
        {
            throw new EngineStartException("The engine is missing from this installation.");
        }

        AppContainerProfile profile;
        try
        {
            profile = AppContainerProfile.CreateOrOpen(AppPaths.ProfileName, "MikePDF engine", "MikePDF sandboxed engine");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or ArgumentException)
        {
            throw new EngineStartException("The sandbox could not be set up: " + e.Message, e);
        }

        return new EngineHost(profile, enginePath, maxDocumentEngines, memoryLimitMb);
    }

    /// <summary>The engine for a document: renders sharp tiles.</summary>
    public EngineSupervisorOptions DocumentOptions() => Options(EngineKind.Document, slotCount: 6, slotSize: 1 << 20);

    /// <summary>The shared pool engine: renders the low-res placeholders (ADR-0006 decision 3), so a slow page parse in the document's engine never blocks them, and the thumbnails, the text, the bookmarks and the search (WP-S2). Its slots hold a thumbnail up to 1 MiB.</summary>
    public EngineSupervisorOptions PoolOptions() => Options(EngineKind.Pool, slotCount: 4, slotSize: 1 << 20);

    /// <summary>
    /// The writer host (ADR-0008): a self-contained .NET process in the same AppContainer, handles in and bytes out. It needs the JIT, so unlike the engine it may make dynamic code. Null when it is not part of
    /// this installation.
    /// </summary>
    public EngineSupervisorOptions? WriterHostOptions()
    {
        var path = Path.Combine(AppPaths.WriterHostSource, "MikePDF.WriterHost.exe");
        if (!File.Exists(path))
        {
            return null;
        }

        return new EngineSupervisorOptions
        {
            EnginePath = path,
            Profile = _profile,
            Peer = ProcessKind.WriterHost,
            Policy = SandboxPolicy.Default with { ProhibitDynamicCode = false, ProcessMemoryLimitBytes = (long)EngineMemoryLimitMb << 20 },
        };
    }

    private EngineSupervisorOptions Options(EngineKind kind, int slotCount, int slotSize) => new()
    {
        EnginePath = _enginePath,
        Profile = _profile,
        Kind = kind,
        Peer = ProcessKind.Engine,
        SlotCount = slotCount,
        SlotSize = slotSize,
        Policy = SandboxPolicy.Default with { ProcessMemoryLimitBytes = (long)EngineMemoryLimitMb << 20 },
    };
}
