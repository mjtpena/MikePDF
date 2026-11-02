using System.Text.Json;

namespace MikePDF.Core.Settings;

/// <summary>
/// What ShellHost needs to know to decide a request (docs/features/shell-extensions.md 3.5, SHELL-15 and SHELL-16): whether thumbnails and previews are on, and how
/// many shell engines may run. It combines the person's settings with the enterprise policies by the rules the app uses everywhere else, and it is <b>read only</b>:
/// unlike <see cref="SettingsStore"/> it never sets a damaged file aside and never writes anything, because ShellHost is a second process that only looks at the
/// app's settings and must not change them.
/// </summary>
public sealed record ShellSwitches(bool ThumbnailsEnabled, bool PreviewEnabled, int MaxEngines)
{
    public const string ThumbnailsKey = "shell.thumbnails";
    public const string PreviewKey = "shell.preview";
    public const string MaxEnginesKey = "shell.maxEngines";

    /// <summary>Everything on and three engines: what applies when nothing is configured and the settings file is missing or unreadable.</summary>
    public static ShellSwitches Defaults { get; } = new(true, true, 3);

    /// <summary>
    /// The effective switches. A locked setting (a policy that is in effect and says so) wins over the person's value; otherwise the person's value, then the default.
    /// </summary>
    public static ShellSwitches Resolve(PolicySnapshot policy, IReadOnlyDictionary<string, object> userValues)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(userValues);
        return new ShellSwitches(
            Effective(policy, userValues, ThumbnailsKey, true),
            Effective(policy, userValues, PreviewKey, true),
            Effective(policy, userValues, MaxEnginesKey, 3));
    }

    /// <summary>Reads the person's three values from <paramref name="settingsPath"/> (missing, oversize or damaged: none) and resolves them against the policies in force.</summary>
    public static ShellSwitches Read(string settingsPath, PolicyProvider policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return Resolve(policy.Current, ReadUserValues(settingsPath));
    }

    /// <summary>The valid values of the shell settings in the file. Never throws, never writes.</summary>
    public static IReadOnlyDictionary<string, object> ReadUserValues(string settingsPath)
    {
        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        try
        {
            var info = new FileInfo(settingsPath);
            if (!info.Exists || info.Length is 0 or > SettingsStore.MaxFileBytes)
            {
                return values;
            }

            using var document = JsonDocument.Parse(File.ReadAllBytes(settingsPath), new JsonDocumentOptions { MaxDepth = SettingsStore.MaxDepth, CommentHandling = JsonCommentHandling.Disallow });
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("values", out var entries) && entries.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in new[] { ThumbnailsKey, PreviewKey, MaxEnginesKey })
                {
                    if (entries.TryGetProperty(key, out var element) && SettingsRegistry.Find(key) is { } definition && definition.TryFromJson(element, out var value))
                    {
                        values[key] = value;
                    }
                }
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            values.Clear();
        }

        return values;
    }

    private static T Effective<T>(PolicySnapshot policy, IReadOnlyDictionary<string, object> userValues, string key, T fallback)
    {
        if (policy.LockFor(key) is { Value: T locked })
        {
            return locked;
        }

        return userValues.TryGetValue(key, out var chosen) && chosen is T value ? value : fallback;
    }
}
