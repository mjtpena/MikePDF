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

    /// <summary>Stored shortcuts that couldn't be read when the app started.</summary>
    public IReadOnlyList<BindingProblem> ShortcutProblems => _sync.Problems;

    /// <summary>The services for a normal run: the real registry for policy, the private folder for files.</summary>
    public static AppServices Create()
    {
        MikePDF.Broker.PrivateDirectory.Ensure(AppPaths.PrivateDirectory);
        var policy = new PolicyProvider(RegistryPolicySource.ForSystem());
        var settings = new SettingsStore(AppPaths.SettingsPath, policy);
        var documents = new DocumentStore(AppPaths.DocumentStorePath);
        LegacyMigration.MigratePageColour(documents, settings);
        return new AppServices(documents, settings, new CommandRegistry(), new CommandHistory(AppPaths.CommandHistoryPath));
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
