using System.Text.Json;
using MikePDF.Core.Commands;

namespace MikePDF.Core.Settings;

/// <summary>Why a file was refused before anything in it was looked at.</summary>
public enum ImportRejection
{
    None,

    /// <summary>Policy turned import and export off (SettingsImportExportEnabled).</summary>
    PolicyBlocked,

    /// <summary>Larger than 256 KiB. Refused without being read.</summary>
    TooLarge,

    /// <summary>Not JSON, too deeply nested, or not an object.</summary>
    NotJson,

    /// <summary>JSON, but not a MikePDF settings (or shortcuts) file.</summary>
    WrongFormat,

    /// <summary>More than 1,000 shortcut entries.</summary>
    TooManyEntries,
}

/// <summary>The JSON form of shortcut overrides, shared by the settings file, the settings export and the shortcuts export.</summary>
public static class KeybindingJson
{
    public static List<KeyBinding> Read(JsonElement array, int max)
    {
        var list = new List<KeyBinding>();
        foreach (var entry in array.EnumerateArray())
        {
            if (list.Count >= max)
            {
                break;
            }

            if (entry.ValueKind == JsonValueKind.Object
                && entry.TryGetProperty("command", out var command) && command.ValueKind == JsonValueKind.String
                && entry.TryGetProperty("chords", out var chords) && chords.ValueKind == JsonValueKind.Array)
            {
                list.Add(new KeyBinding(command.GetString()!, [.. chords.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.String).Select(c => c.GetString()!).Take(8)]));
            }
        }

        return list;
    }

    public static void Write(Utf8JsonWriter writer, IEnumerable<KeyBinding> bindings)
    {
        writer.WriteStartArray("keybindings");
        foreach (var binding in bindings)
        {
            writer.WriteStartObject();
            writer.WriteString("command", binding.Command);
            writer.WriteStartArray("chords");
            foreach (var chord in binding.Chords)
            {
                writer.WriteStringValue(chord);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }
}

/// <summary>What importing a settings file would do, counted before anything changes (docs/features/settings-policies.md 3.7).</summary>
public sealed class SettingsImportPreview
{
    public ImportRejection Rejection { get; init; }

    public bool NewerVersion { get; init; }

    /// <summary>Settings whose value would change: key and new value.</summary>
    public IReadOnlyList<(string Key, object Value)> Changes { get; init; } = [];

    /// <summary>Settings in the file that policy controls, which can't be imported.</summary>
    public IReadOnlyList<string> Locked { get; init; } = [];

    /// <summary>Keys this version doesn't know.</summary>
    public IReadOnlyList<string> Unknown { get; init; } = [];

    /// <summary>Known keys with a wrong type or a value out of range. Never clamped.</summary>
    public IReadOnlyList<string> Invalid { get; init; } = [];

    /// <summary>The shortcuts that would be in force after the import, or null when the file has no shortcut list.</summary>
    public IReadOnlyList<KeyBinding>? Keybindings { get; init; }

    public IReadOnlyList<BindingProblem> BindingProblems { get; init; } = [];

    public bool IsAccepted => Rejection == ImportRejection.None;
}

/// <summary>
/// Export and import of the settings file (<c>MikePDF-settings.json</c>) and of the shortcuts alone (<c>MikePDF-shortcuts.json</c>). Import is two steps:
/// <see cref="Preview"/> reads and checks without changing anything, <see cref="Apply"/> does it. Apply first keeps the current file as settings.prev.json so
/// <see cref="Undo"/> can put it back exactly.
/// </summary>
public static class SettingsTransfer
{
    public const string SettingsFormat = "mikepdf-settings";
    public const string ShortcutsFormat = "mikepdf-shortcuts";
    public const int FormatVersion = 1;
    public const long MaxImportBytes = 256 * 1024;
    public const int MaxEntries = 1000;

    public static string PreviousFile(SettingsStore store) => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(store.Path)!, "settings.prev.json");

    /// <summary>The exportable values the person chose (policy-locked ones and history are not included) and their shortcuts.</summary>
    public static byte[] ExportSettings(SettingsStore store)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("format", SettingsFormat);
            writer.WriteNumber("schemaVersion", FormatVersion);
            writer.WriteStartObject("values");
            foreach (var (key, value) in store.UserValues.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var definition = SettingsRegistry.Find(key);
                if (definition is { Exported: true } && !store.Effective(key).Locked)
                {
                    writer.WritePropertyName(key);
                    SettingDefinition.WriteJson(writer, value);
                }
            }

            writer.WriteEndObject();
            KeybindingJson.Write(writer, store.Keybindings);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    public static byte[] ExportShortcuts(CommandRegistry registry)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("format", ShortcutsFormat);
            writer.WriteNumber("schemaVersion", FormatVersion);
            KeybindingJson.Write(writer, registry.Overrides);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <summary>Reads a stream for import, refusing a long one before reading any of it. Null if refused (see <paramref name="rejection"/>).</summary>
    public static byte[]? ReadLimited(Stream stream, out ImportRejection rejection)
    {
        rejection = ImportRejection.None;
        if (stream.CanSeek && stream.Length > MaxImportBytes)
        {
            rejection = ImportRejection.TooLarge;
            return null;
        }

        // One buffer, one byte longer than the limit: if it fills, the file is too long, and no more than this (about 256 KiB) has been allocated.
        var buffer = new byte[(stream.CanSeek ? Math.Min(stream.Length, MaxImportBytes) : MaxImportBytes) + 1];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0)
        {
            total += read;
        }

        if (total > MaxImportBytes)
        {
            rejection = ImportRejection.TooLarge;
            return null;
        }

        return buffer.AsSpan(0, total).ToArray();
    }

    public static SettingsImportPreview Preview(ReadOnlySpan<byte> bytes, SettingsStore store, CommandRegistry registry)
    {
        if (!store.Policy.Current.ImportExportAllowed)
        {
            return new SettingsImportPreview { Rejection = ImportRejection.PolicyBlocked };
        }

        if (bytes.Length > MaxImportBytes)
        {
            return new SettingsImportPreview { Rejection = ImportRejection.TooLarge };
        }

        if (!TryParse(bytes, SettingsFormat, out var document, out var rejection, out var newer))
        {
            return new SettingsImportPreview { Rejection = rejection };
        }

        using (document)
        {
            var root = document.RootElement;
            var changes = new List<(string, object)>();
            var locked = new List<string>();
            var unknown = new List<string>();
            var invalid = new List<string>();
            if (root.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in values.EnumerateObject())
                {
                    var definition = SettingsRegistry.Find(property.Name);
                    if (definition is null)
                    {
                        unknown.Add(property.Name);
                    }
                    else if (store.Effective(definition.Key).Locked)
                    {
                        locked.Add(definition.Key);
                    }
                    else if (!definition.TryFromJson(property.Value, out var value))
                    {
                        invalid.Add(definition.Key);
                    }
                    else if (!value.Equals(store.Effective(definition.Key).Value))
                    {
                        changes.Add((definition.Key, value));
                    }
                }
            }

            IReadOnlyList<KeyBinding>? bindings = null;
            IReadOnlyList<BindingProblem> problems = [];
            if (root.TryGetProperty("keybindings", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                if (array.GetArrayLength() > MaxEntries)
                {
                    return new SettingsImportPreview { Rejection = ImportRejection.TooManyEntries };
                }

                var validation = registry.ValidateOverrides(KeybindingJson.Read(array, MaxEntries), strict: true);
                bindings = [.. validation.Accepted.Select(a => new KeyBinding(a.Command, [.. a.Chords.Select(c => c.ToString())]))];
                problems = validation.Problems;
            }

            return new SettingsImportPreview
            {
                NewerVersion = newer,
                Changes = changes,
                Locked = locked,
                Unknown = unknown,
                Invalid = invalid,
                Keybindings = bindings,
                BindingProblems = problems,
            };
        }
    }

    /// <summary>What a shortcuts file would do: the entries that would stand, and the ones skipped and why.</summary>
    public static SettingsImportPreview PreviewShortcuts(ReadOnlySpan<byte> bytes, SettingsStore store, CommandRegistry registry)
    {
        if (!store.Policy.Current.ImportExportAllowed)
        {
            return new SettingsImportPreview { Rejection = ImportRejection.PolicyBlocked };
        }

        if (bytes.Length > MaxImportBytes)
        {
            return new SettingsImportPreview { Rejection = ImportRejection.TooLarge };
        }

        if (!TryParse(bytes, ShortcutsFormat, out var document, out var rejection, out var newer))
        {
            return new SettingsImportPreview { Rejection = rejection };
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("keybindings", out var array) || array.ValueKind != JsonValueKind.Array)
            {
                return new SettingsImportPreview { Rejection = ImportRejection.WrongFormat };
            }

            if (array.GetArrayLength() > MaxEntries)
            {
                return new SettingsImportPreview { Rejection = ImportRejection.TooManyEntries };
            }

            var validation = registry.ValidateOverrides(KeybindingJson.Read(array, MaxEntries), strict: true);
            return new SettingsImportPreview
            {
                NewerVersion = newer,
                Keybindings = [.. validation.Accepted.Select(a => new KeyBinding(a.Command, [.. a.Chords.Select(c => c.ToString())]))],
                BindingProblems = validation.Problems,
            };
        }
    }

    /// <summary>
    /// Applies a preview that was accepted: keeps the current settings file as settings.prev.json, then changes the values and replaces the shortcuts. The
    /// shortcuts are changed in the store; the registry follows through <see cref="KeybindingSync"/>.
    /// </summary>
    public static void Apply(SettingsImportPreview preview, SettingsStore store)
    {
        if (!preview.IsAccepted || !store.Policy.Current.ImportExportAllowed)
        {
            return;
        }

        store.Flush();
        store.WriteNow();
        File.Copy(store.Path, PreviousFile(store), overwrite: true);
        var values = new Dictionary<string, object>(store.UserValues);
        foreach (var (key, value) in preview.Changes)
        {
            values[key] = value;
        }

        store.Replace(values, preview.Keybindings);
    }

    public static bool CanUndo(SettingsStore store) => File.Exists(PreviousFile(store));

    /// <summary>Puts the file from before the last import back, exactly, and forgets it. False if there is nothing to undo.</summary>
    public static bool Undo(SettingsStore store)
    {
        var previous = PreviousFile(store);
        if (!File.Exists(previous) || !store.Policy.Current.ImportExportAllowed)
        {
            return false;
        }

        store.Reload(File.ReadAllBytes(previous));
        File.Delete(previous);
        return true;
    }

    private static bool TryParse(ReadOnlySpan<byte> bytes, string format, out JsonDocument document, out ImportRejection rejection, out bool newerVersion)
    {
        document = null!;
        newerVersion = false;
        try
        {
            document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = SettingsStore.MaxDepth, CommentHandling = JsonCommentHandling.Disallow });
        }
        catch (JsonException)
        {
            rejection = ImportRejection.NotJson;
            return false;
        }

        var root = document.RootElement;
        var isObject = root.ValueKind == JsonValueKind.Object;
        if (!isObject || !root.TryGetProperty("format", out var f) || f.ValueKind != JsonValueKind.String || f.GetString() != format)
        {
            document.Dispose();
            rejection = isObject ? ImportRejection.WrongFormat : ImportRejection.NotJson;
            return false;
        }

        newerVersion = root.TryGetProperty("schemaVersion", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var version) && version > FormatVersion;
        rejection = ImportRejection.None;
        return true;
    }
}

/// <summary>Keeps the shortcut registry and the settings file in step: the registry's overrides are stored, and a change to the stored shortcuts (an import, an undo) reaches the registry.</summary>
public sealed class KeybindingSync : IDisposable
{
    private readonly CommandRegistry _registry;
    private readonly SettingsStore _store;
    private bool _busy;

    public KeybindingSync(CommandRegistry registry, SettingsStore store)
    {
        _registry = registry;
        _store = store;
        Problems = registry.LoadOverrides(store.Keybindings);
        registry.BindingsChanged += OnRegistryChanged;
        store.SettingsChanged += OnStoreChanged;
    }

    /// <summary>Stored shortcuts that couldn't be read when the app started ("Couldn't read a shortcut").</summary>
    public IReadOnlyList<BindingProblem> Problems { get; private set; }

    public void Dispose()
    {
        _registry.BindingsChanged -= OnRegistryChanged;
        _store.SettingsChanged -= OnStoreChanged;
    }

    private void OnRegistryChanged()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            _store.SetKeybindings(_registry.Overrides);
        }
        finally
        {
            _busy = false;
        }
    }

    private void OnStoreChanged(IReadOnlyList<string> keys)
    {
        if (_busy || !keys.Contains("keybindings"))
        {
            return;
        }

        _busy = true;
        try
        {
            Problems = _registry.LoadOverrides(_store.Keybindings);
        }
        finally
        {
            _busy = false;
        }
    }
}
