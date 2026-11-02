using Quire.Broker;
using Quire.Protocol;

namespace Quire.App.Services;

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

    private EngineHost(AppContainerProfile profile, string enginePath)
    {
        _profile = profile;
        _enginePath = enginePath;
    }

    public static EngineHost Create()
    {
        if (!AppPaths.IsPackaged)
        {
            // Without package identity the child gets no access to the engine's directory (nothing grants it): development builds are run as a registered layout.
            throw new EngineStartException("Quire runs as an installed package; register a build with Add-AppxPackage -Register, or install a package from build/package.ps1.");
        }

        var enginePath = Path.Combine(AppPaths.EngineSource, "Quire.Engine.exe");
        if (!File.Exists(enginePath) || !File.Exists(Path.Combine(AppPaths.EngineSource, "pdfium.dll")))
        {
            throw new EngineStartException("The engine is missing from this installation.");
        }

        AppContainerProfile profile;
        try
        {
            profile = AppContainerProfile.CreateOrOpen(AppPaths.ProfileName, "Quire engine", "Quire sandboxed engine");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or ArgumentException)
        {
            throw new EngineStartException("The sandbox could not be set up: " + e.Message, e);
        }

        return new EngineHost(profile, enginePath);
    }

    /// <summary>The engine for a document: renders sharp tiles.</summary>
    public EngineSupervisorOptions DocumentOptions() => Options(EngineKind.Document, slotCount: 6, slotSize: 1 << 20);

    /// <summary>The shared pool engine: renders the low-res placeholders (ADR-0006 decision 3), so a slow page parse in the document's engine never blocks them, and the thumbnails, the text, the bookmarks and the search (WP-S2). Its slots hold a thumbnail up to 1 MiB.</summary>
    public EngineSupervisorOptions PoolOptions() => Options(EngineKind.Pool, slotCount: 4, slotSize: 1 << 20);

    private EngineSupervisorOptions Options(EngineKind kind, int slotCount, int slotSize) => new()
    {
        EnginePath = _enginePath,
        Profile = _profile,
        Kind = kind,
        Peer = ProcessKind.Engine,
        SlotCount = slotCount,
        SlotSize = slotSize,
    };
}
