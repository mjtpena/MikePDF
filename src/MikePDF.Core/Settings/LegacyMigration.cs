using MikePDF.Core.Colour;
using MikePDF.Core.Persistence;

namespace MikePDF.Core.Settings;

/// <summary>
/// Moves what earlier versions kept in documents.json into the settings file. Recent files and remembered positions are history, not settings, and stay where
/// they are; the page colour was the one setting, and it moves on the first run that has no settings file yet.
/// </summary>
public static class LegacyMigration
{
    /// <summary>Copies the page colour from the old store when there is no settings file. Returns true if it moved a value.</summary>
    public static bool MigratePageColour(DocumentStore documents, SettingsStore settings)
    {
        if (settings.FileExisted)
        {
            return false;
        }

        var legacy = documents.PageColour;
        if (legacy == PageColourMode.Normal || settings.Set("view.pageColour", legacy.ToString()) != SetResult.Applied)
        {
            return false;
        }

        settings.Flush();
        return true;
    }
}
