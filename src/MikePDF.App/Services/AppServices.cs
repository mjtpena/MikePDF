using MikePDF.Core.Commands;
using MikePDF.Core.Persistence;
using MikePDF.Core.Settings;

namespace MikePDF.App.Services;

/// <summary>
/// The pieces the window shares: the document store (history), the settings, the policies, the commands and their shortcuts, and the command history.
/// Built once at launch. Everything here lives in the private folder (settings.json, palette-history.json, documents.json) and nothing is sent anywhere.
/// </summary>
internal sealed class AppServices : IDisposable
{
    private readonly KeybindingSync _sync;

    public AppServices(DocumentStore documents, SettingsStore settings, CommandRegistry commands, CommandHistory history)
    {
        Documents = documents;
        Settings = settings;
        Commands = commands;
        History = history;
        Dispatcher = new ChordDispatcher(commands);
        _sync = new KeybindingSync(commands, settings);
    }

    public DocumentStore Documents { get; }

    public SettingsStore Settings { get; }

    public PolicyProvider Policy => Settings.Policy;

    public CommandRegistry Commands { get; }

    public ChordDispatcher Dispatcher { get; }

    public CommandHistory History { get; }

    /// <summary>Asks for the PDFium version of the engine (set by the host); null before there is a host.</summary>
    public Func<Task<string?>>? PdfiumVersion { get; set; }

    /// <summary>Stored shortcuts that couldn't be read when the app started.</summary>
    public IReadOnlyList<BindingProblem> ShortcutProblems => _sync.Problems;

    private const string TestPolicyRoot = @"Software\MikePDF.Tests\";

    /// <summary>The services for a normal run: the real registry for policy, the private folder for files.</summary>
    public static AppServices Create()
    {
        MikePDF.Broker.PrivateDirectory.Ensure(AppPaths.PrivateDirectory);
        var settings = new SettingsStore(AppPaths.SettingsPath, OpenPolicy());
        var documents = new DocumentStore(AppPaths.DocumentStorePath);
        LegacyMigration.MigratePageColour(documents, settings);
        return new AppServices(documents, settings, new CommandRegistry(), new CommandHistory(AppPaths.CommandHistoryPath));
    }

    /// <summary>
    /// Reads the administrator's policy from the registry (machine and user level, plus the test seam below). <see cref="Create"/> uses it, and so does <see cref="EngineHost"/>,
    /// which needs the engine memory limit before the services exist.
    /// </summary>
    internal static PolicyProvider OpenPolicy()
    {
        // Test seam: a journey names a throwaway key under HKCU\Software\MikePDF.Tests, which is read as more user-level policy. User-level policy can only make
        // things stricter, so this cannot weaken anything. (Windows does not let a standard user write HKCU\Software\Policies, so a test cannot use the real key.)
        var test = TestSeams.Get(MikePDF.Core.TestSeamGate.PolicyKey);
        IReadOnlyList<string> extra = test is { Length: < 200 } && test.StartsWith(TestPolicyRoot, StringComparison.OrdinalIgnoreCase) && !test.Contains("..", StringComparison.Ordinal) ? [test] : [];
        return new PolicyProvider(new RegistryPolicySource(Microsoft.Win32.Registry.LocalMachine, PolicyCatalog.RegistryPath, Microsoft.Win32.Registry.CurrentUser, PolicyCatalog.RegistryPath, extra));
    }

    /// <summary>Applies the settings that live in the document store.</summary>
    public void ApplyToDocuments()
    {
        Documents.RecentLimit = Settings.Value<int>("recent.maxItems");
        Documents.RestorePositions = Settings.Value<bool>("view.restorePosition");
    }

    public void Dispose()
    {
        _sync.Dispose();
        Settings.Dispose();
    }
}
