using System.Globalization;
using System.Text.Json;

namespace MikePDF.Core.Settings;

public enum SettingKind
{
    Bool,
    Number,
    Text,

    /// <summary>One of a fixed list of names, stored as text so that a value written by another version never makes the file unreadable.</summary>
    Enum,
}

/// <summary>Which page of Settings a setting appears on, or <see cref="None"/> for one that has no control yet.</summary>
public enum SettingSection
{
    None,
    General,
    Reading,
    Privacy,
}

/// <summary>
/// One setting: its key, type, default and the rules a value must meet. Everything else about it (the control, the export, the import check, the docs) is
/// derived from this record (docs/features/settings-policies.md 3.1), so adding a setting is one entry here and its two strings.
/// </summary>
public sealed class SettingDefinition
{
    public required string Key { get; init; }

    public required SettingKind Kind { get; init; }

    /// <summary>A bool, an int, or a string (the name, for an enum).</summary>
    public required object Default { get; init; }

    /// <summary>The spec that owns the setting ("settings", "tabs", "save", ...). Only for people reading the registry.</summary>
    public required string Owner { get; init; }

    public int Min { get; init; }

    public int Max { get; init; } = int.MaxValue;

    public IReadOnlyList<string>? Values { get; init; }

    public int MaxLength { get; init; } = 128;

    /// <summary>Written by Export settings. History and device state are not.</summary>
    public bool Exported { get; init; } = true;

    public bool RestartNeeded { get; init; }

    /// <summary>The policy that can lock it (a name in <see cref="PolicyCatalog"/>).</summary>
    public string? PolicyName { get; init; }

    public SettingSection Section { get; init; }

    public string NameKey => "Setting_" + Key.Replace('.', '_') + "_Name";

    public string DescriptionKey => "Setting_" + Key.Replace('.', '_') + "_Description";

    /// <summary>The words for one value of an enum setting: "Setting_{key}_Value_{value}".</summary>
    public string ValueKey(string value) => "Setting_" + Key.Replace('.', '_') + "_Value_" + value;

    /// <summary>Whether a value is of the right type and within the setting's rules.</summary>
    public bool IsValid(object? value) => Kind switch
    {
        SettingKind.Bool => value is bool,
        SettingKind.Number => value is int i && i >= Min && i <= Max,
        SettingKind.Text => value is string s && s.Length <= MaxLength,
        SettingKind.Enum => value is string e && Values!.Contains(e, StringComparer.Ordinal),
        _ => false,
    };

    /// <summary>Reads a JSON value as this setting's type. False for a wrong type or a value outside the rules; nothing is clamped.</summary>
    public bool TryFromJson(JsonElement element, out object value)
    {
        value = Default;
        object? candidate = Kind switch
        {
            SettingKind.Bool when element.ValueKind is JsonValueKind.True or JsonValueKind.False => element.GetBoolean(),
            SettingKind.Number when element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var i) => i,
            SettingKind.Text or SettingKind.Enum when element.ValueKind == JsonValueKind.String => element.GetString(),
            _ => null,
        };
        if (candidate is null || !IsValid(candidate))
        {
            return false;
        }

        value = candidate;
        return true;
    }

    public static void WriteJson(Utf8JsonWriter writer, object value)
    {
        switch (value)
        {
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case int i:
                writer.WriteNumberValue(i);
                break;
            default:
                writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }
    }

    public override string ToString() => Key;
}

/// <summary>The settings that exist in this build. A key that a later feature owns is added by that feature, with its control.</summary>
public static class SettingsRegistry
{
    public static IReadOnlyList<SettingDefinition> All { get; } =
    [
        new SettingDefinition
        {
            Key = "general.theme",
            Kind = SettingKind.Enum,
            Default = "System",
            Values = ["System", "Light", "Dark"],
            Owner = "settings",
            Section = SettingSection.General,
        },
        new SettingDefinition
        {
            Key = "startup.restore",
            Kind = SettingKind.Enum,
            Default = "Ask",
            Values = ["Always", "Ask", "Never"],
            Owner = "tabs",
            Section = SettingSection.General,
        },
        new SettingDefinition
        {
            Key = "windows.openFromShell",
            Kind = SettingKind.Enum,
            Default = "LastActiveWindow",
            Values = ["LastActiveWindow", "NewWindow"],
            Owner = "tabs",
            Section = SettingSection.General,
        },
        new SettingDefinition
        {
            Key = "view.pageColour",
            Kind = SettingKind.Enum,
            Default = "Normal",
            Values = ["Normal", "Inverted", "Sepia"],
            Owner = "settings",
            Section = SettingSection.Reading,
        },
        new SettingDefinition
        {
            Key = "view.restorePosition",
            Kind = SettingKind.Bool,
            Default = true,
            Owner = "settings",
            Section = SettingSection.Reading,
        },
        new SettingDefinition
        {
            Key = "recent.maxItems",
            Kind = SettingKind.Number,
            Default = 20,
            Min = 0,
            Max = 50,
            Owner = "settings",
            Section = SettingSection.Privacy,
        },
        new SettingDefinition
        {
            Key = "privacy.addToWindowsRecent",
            Kind = SettingKind.Bool,
            Default = true,
            Owner = "file association",
            Section = SettingSection.Privacy,
        },
        new SettingDefinition
        {
            Key = "links.behaviour",
            Kind = SettingKind.Enum,
            Default = "ShowAddress",
            Values = ["Block", "ShowAddress", "Confirm"],
            Owner = "settings",
            PolicyName = "ExternalLinks",
            Section = SettingSection.Privacy,
        },
        new SettingDefinition
        {
            Key = "save.mode",
            Kind = SettingKind.Enum,
            Default = "InPlace",
            Values = ["InPlace", "AlwaysSaveAsCopy"],
            Owner = "save pipeline",
            PolicyName = "SaveMode",
        },
        new SettingDefinition
        {
            Key = "markup.authorName",
            Kind = SettingKind.Text,
            Default = string.Empty,
            Owner = "annotation core",
            PolicyName = "AnnotationAuthorName",
        },
        new SettingDefinition
        {
            Key = "shell.thumbnails",
            Kind = SettingKind.Bool,
            Default = true,
            Owner = "shell extensions",
            PolicyName = "ShellThumbnailsEnabled",
            Section = SettingSection.Privacy,
        },
        new SettingDefinition
        {
            Key = "shell.preview",
            Kind = SettingKind.Bool,
            Default = true,
            Owner = "shell extensions",
            PolicyName = "ShellPreviewEnabled",
            Section = SettingSection.Privacy,
        },
        new SettingDefinition
        {
            Key = "shell.maxEngines",
            Kind = SettingKind.Number,
            Default = 3,
            Min = 1,
            Max = 6,
            Owner = "shell extensions",
            PolicyName = "ShellMaxEngines",
        },
        new SettingDefinition
        {
            Key = "engines.maxLiveTabs",
            Kind = SettingKind.Number,
            Default = 6,
            Min = 1,
            Max = 32,
            Owner = "tabs",
            PolicyName = "MaxLiveTabs",
        },
    ];

    public static SettingDefinition? Find(string key) => All.FirstOrDefault(s => s.Key == key);
}
