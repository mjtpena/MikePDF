using System.Globalization;
using System.Text.Json;
using MikePDF.Core.Commands;

namespace MikePDF.Core.Settings;

public enum SettingSource
{
    Default,
    User,
    Policy,
}

/// <summary>The value a setting has now, where it came from, and whether the person may change it.</summary>
public sealed record EffectiveSetting(string Key, object Value, SettingSource Source, bool Locked, string? PolicyName)
{
    public string Text => Convert.ToString(Value, CultureInfo.InvariantCulture) ?? string.Empty;
}

public enum SetResult
{
    Applied,
    Unchanged,
    Locked,
    Invalid,
    UnknownKey,
}

/// <summary>
/// The settings file, <c>LocalState\private\settings.json</c> (docs/features/settings-policies.md 3.1 and 3.2). Policy beats the person beats the default.
/// A change applies at once, in every window, through <see cref="SettingsChanged"/>, and is written 500 ms later, atomically. A damaged file is kept aside and
/// defaults are used; one bad value never costs the others; keys this version does not know are kept when the file is rewritten. Thread-safe.
/// </summary>
public sealed class SettingsStore : IDisposable
{
    public const int SchemaVersion = 1;
    public const long MaxFileBytes = 1024 * 1024;
    public const int MaxKeys = 256;
    public const int MaxDepth = 8;
    public const int MaxKeepCorrupt = 3;
    public const int MaxKeybindings = 1000;
    public static readonly TimeSpan WriteDelay = TimeSpan.FromMilliseconds(500);

    private readonly object _gate = new();
    private readonly string _path;
    private readonly PolicyProvider _policy;
    private readonly TimeProvider _time;
    private readonly ITimer _timer;
    private readonly Dictionary<string, object> _user = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonElement> _unknown = new(StringComparer.Ordinal);
    private Dictionary<string, (string Text, bool Locked)> _lastEffective = [];
    private List<KeyBinding> _keybindings = [];
    private int _fileVersion = SchemaVersion;
    private bool _dirty;
    private bool _corruptNoticePending;

    public SettingsStore(string path, PolicyProvider? policy = null, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _path = path;
        _policy = policy ?? PolicyProvider.None();
        _time = time ?? TimeProvider.System;
        _timer = _time.CreateTimer(_ => Flush(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        FileExisted = Load();
        _lastEffective = EffectiveSnapshot();
        _policy.Changed += OnPolicyChanged;
    }

    /// <summary>Raised after settings changed, with the keys (a change by policy lists the keys it changed; "keybindings" for the shortcuts). Not raised for a write that changes nothing.</summary>
    public event Action<IReadOnlyList<string>>? SettingsChanged;

    /// <summary>Raised when the file could not be written. The settings still apply for this run.</summary>
    public event Action? SaveFailed;

    /// <summary>The settings file was there when the store opened. False on a first run, which is when old data is migrated.</summary>
    public bool FileExisted { get; }

    public bool LastSaveFailed { get; private set; }

    public PolicyProvider Policy => _policy;

    /// <summary>True once after a damaged file was set aside, so the notice is shown one time.</summary>
    public bool TakeCorruptNotice()
    {
        lock (_gate)
        {
            var pending = _corruptNoticePending;
            _corruptNoticePending = false;
            return pending;
        }
    }

    public IReadOnlyList<KeyBinding> Keybindings
    {
        get
        {
            lock (_gate)
            {
                return [.. _keybindings];
            }
        }
    }

    public void SetKeybindings(IReadOnlyList<KeyBinding> bindings)
    {
        lock (_gate)
        {
            var next = bindings.Take(MaxKeybindings).Select(b => new KeyBinding(b.Command, [.. b.Chords])).ToList();
            if (SameBindings(next, _keybindings))
            {
                return;
            }

            _keybindings = next;
            MarkDirty();
        }

        SettingsChanged?.Invoke(["keybindings"]);
    }

    /// <summary>What a setting is now. An unknown key is a programming error.</summary>
    public EffectiveSetting Effective(string key)
    {
        var definition = SettingsRegistry.Find(key) ?? throw new ArgumentException("No such setting: " + key, nameof(key));
        return EffectiveOf(definition);
    }

    public T Value<T>(string key) => (T)Effective(key).Value;

    /// <summary>The value the person chose, ignoring policy, or null if they never did.</summary>
    public object? UserValue(string key)
    {
        lock (_gate)
        {
            return _user.GetValueOrDefault(key);
        }
    }

    public IReadOnlyDictionary<string, object> UserValues
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, object>(_user);
            }
        }
    }

    public SetResult Set(string key, object value)
    {
        var definition = SettingsRegistry.Find(key);
        if (definition is null)
        {
            return SetResult.UnknownKey;
        }

        if (!definition.IsValid(value))
        {
            return SetResult.Invalid;
        }

        lock (_gate)
        {
            if (EffectiveOf(definition).Locked)
            {
                return SetResult.Locked;
            }

            if (_user.TryGetValue(key, out var current) && current.Equals(value))
            {
                return SetResult.Unchanged;
            }

            if (!_user.ContainsKey(key) && definition.Default.Equals(value))
            {
                return SetResult.Unchanged;
            }

            _user[key] = value;
            MarkDirty();
        }

        _lastEffective = EffectiveSnapshot();
        SettingsChanged?.Invoke([key]);
        return SetResult.Applied;
    }

    /// <summary>Puts every setting the person may change back to its default. A locked setting is left alone. Returns how many changed.</summary>
    public int ResetAll()
    {
        List<string> changed;
        lock (_gate)
        {
            changed = [.. _user.Keys.Where(k => SettingsRegistry.Find(k) is { } d && !EffectiveOf(d).Locked)];
            foreach (var key in changed)
            {
                _user.Remove(key);
            }

            if (changed.Count > 0)
            {
                MarkDirty();
            }
        }

        if (changed.Count > 0)
        {
            _lastEffective = EffectiveSnapshot();
            SettingsChanged?.Invoke(changed);
        }

        return changed.Count;
    }

    /// <summary>
    /// Replaces the person's values and shortcuts with these (used by Import and by Undo import). Values for unknown, locked or invalid settings are skipped by
    /// the caller; this is all or nothing for what it is given.
    /// </summary>
    public void Replace(IReadOnlyDictionary<string, object> values, IReadOnlyList<KeyBinding>? keybindings)
    {
        List<string> changed = [];
        lock (_gate)
        {
            foreach (var key in _user.Keys.Concat(values.Keys).Distinct())
            {
                var had = _user.TryGetValue(key, out var before);
                var has = values.TryGetValue(key, out var after);
                if (had != has || (had && !before!.Equals(after)))
                {
                    changed.Add(key);
                }
            }

            _user.Clear();
            foreach (var (key, value) in values)
            {
                _user[key] = value;
            }

            if (keybindings is not null)
            {
                var next = keybindings.Take(MaxKeybindings).Select(b => new KeyBinding(b.Command, [.. b.Chords])).ToList();
                if (!SameBindings(next, _keybindings))
                {
                    _keybindings = next;
                    changed.Add("keybindings");
                }
            }

            if (changed.Count > 0)
            {
                MarkDirty();
            }
        }

        if (changed.Count > 0)
        {
            _lastEffective = EffectiveSnapshot();
            SettingsChanged?.Invoke(changed);
        }
    }

    /// <summary>The settings file.</summary>
    public string Path => _path;

    /// <summary>Writes the file now even if nothing changed, so that there is a file to keep (before an import).</summary>
    public bool WriteNow()
    {
        lock (_gate)
        {
            _dirty = true;
        }

        return Flush();
    }

    /// <summary>
    /// Puts the given file contents in place of the settings file, exactly, and reads them again: used by Undo import. Settings that changed are reported through
    /// <see cref="SettingsChanged"/>.
    /// </summary>
    public void Reload(byte[] contents)
    {
        List<string> changed;
        lock (_gate)
        {
            var before = new Dictionary<string, object>(_user);
            var beforeBindings = _keybindings;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _dirty = false;
            AtomicFile.Write(_path, contents);
            _user.Clear();
            _unknown.Clear();
            _keybindings = [];
            Load();
            changed = [.. before.Keys.Concat(_user.Keys).Distinct().Where(k => before.GetValueOrDefault(k) is not { } b || !_user.TryGetValue(k, out var a) || !a.Equals(b))];
            if (!SameBindings(beforeBindings, _keybindings))
            {
                changed.Add("keybindings");
            }
        }

        if (changed.Count > 0)
        {
            _lastEffective = EffectiveSnapshot();
            SettingsChanged?.Invoke(changed);
        }
    }

    /// <summary>Writes any pending change now. Returns false if the write failed.</summary>
    public bool Flush()
    {
        byte[] bytes;
        lock (_gate)
        {
            if (!_dirty)
            {
                return !LastSaveFailed;
            }

            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            bytes = Serialise();
            _dirty = false;
        }

        try
        {
            AtomicFile.Write(_path, bytes);
            LastSaveFailed = false;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            lock (_gate)
            {
                _dirty = true;
            }

            LastSaveFailed = true;
            SaveFailed?.Invoke();
            return false;
        }
    }

    public void Dispose()
    {
        _policy.Changed -= OnPolicyChanged;
        Flush();
        _timer.Dispose();
    }

    // ---- reading -----------------------------------------------------------------------------------------------------------------------

    private EffectiveSetting EffectiveOf(SettingDefinition definition)
    {
        if (_policy.Current.LockFor(definition.Key) is { } locked && definition.IsValid(locked.Value))
        {
            return new EffectiveSetting(definition.Key, locked.Value, SettingSource.Policy, true, locked.Entry.Definition.Name);
        }

        lock (_gate)
        {
            return _user.TryGetValue(definition.Key, out var value)
                ? new EffectiveSetting(definition.Key, value, SettingSource.User, false, definition.PolicyName)
                : new EffectiveSetting(definition.Key, definition.Default, SettingSource.Default, false, definition.PolicyName);
        }
    }

    private Dictionary<string, (string, bool)> EffectiveSnapshot() =>
        SettingsRegistry.All.ToDictionary(d => d.Key, d => { var e = EffectiveOf(d); return (e.Text, e.Locked); }, StringComparer.Ordinal);

    private void OnPolicyChanged()
    {
        var now = EffectiveSnapshot();
        var changed = now.Where(p => !_lastEffective.TryGetValue(p.Key, out var before) || before != p.Value).Select(p => p.Key).ToList();
        _lastEffective = now;
        if (changed.Count > 0)
        {
            SettingsChanged?.Invoke(changed);
        }
    }

    private bool Load()
    {
        if (!File.Exists(_path))
        {
            return false;
        }

        try
        {
            var info = new FileInfo(_path);
            if (info.Length is 0 or > MaxFileBytes)
            {
                SetAsideCorrupt();
                return true;
            }

            var bytes = File.ReadAllBytes(_path);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                // The spec lists a file with a byte-order mark among the damaged ones (SET-05): this app never writes one, so someone else did.
                SetAsideCorrupt();
                return true;
            }

            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = MaxDepth, CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                SetAsideCorrupt();
                return true;
            }

            Read(document.RootElement);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SetAsideCorrupt();
        }

        return true;
    }

    private void Read(JsonElement root)
    {
        if (root.TryGetProperty("schemaVersion", out var version) && version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out var v) && v > 0)
        {
            _fileVersion = Math.Max(v, SchemaVersion);
        }

        var keys = 0;
        if (root.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in values.EnumerateObject())
            {
                if (++keys > MaxKeys)
                {
                    break;
                }

                var definition = SettingsRegistry.Find(property.Name);
                if (definition is null)
                {
                    _unknown[property.Name] = property.Value.Clone(); // kept, so a downgrade does not erase a newer version's settings
                }
                else if (definition.TryFromJson(property.Value, out var value))
                {
                    _user[definition.Key] = value;
                } // else: a wrong type or a value out of range falls back to the default for this key only
            }
        }

        if (root.TryGetProperty("keybindings", out var bindings) && bindings.ValueKind == JsonValueKind.Array)
        {
            _keybindings = KeybindingJson.Read(bindings, MaxKeybindings);
        }
    }

    private void SetAsideCorrupt()
    {
        _corruptNoticePending = true;
        try
        {
            var directory = System.IO.Path.GetDirectoryName(_path)!;
            var stamp = _time.GetUtcNow().ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture);
            File.Move(_path, System.IO.Path.Combine(directory, "settings.corrupt-" + stamp + ".json"), overwrite: true);
            foreach (var old in Directory.EnumerateFiles(directory, "settings.corrupt-*.json").OrderByDescending(f => f, StringComparer.Ordinal).Skip(MaxKeepCorrupt))
            {
                File.Delete(old);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The damaged file stays where it is; the next write replaces it. Defaults are used either way.
        }
    }

    // ---- writing -----------------------------------------------------------------------------------------------------------------------

    private void MarkDirty()
    {
        _dirty = true;
        _timer.Change(WriteDelay, Timeout.InfiniteTimeSpan);
    }

    private byte[] Serialise()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", _fileVersion);
            writer.WriteStartObject("values");
            var written = 0;
            foreach (var key in _user.Keys.Order(StringComparer.Ordinal))
            {
                writer.WritePropertyName(key);
                SettingDefinition.WriteJson(writer, _user[key]);
                written++;
            }

            foreach (var key in _unknown.Keys.Order(StringComparer.Ordinal).Where(k => !_user.ContainsKey(k)))
            {
                if (written++ >= MaxKeys)
                {
                    break;
                }

                writer.WritePropertyName(key);
                _unknown[key].WriteTo(writer);
            }

            writer.WriteEndObject();
            KeybindingJson.Write(writer, _keybindings);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static bool SameBindings(List<KeyBinding> a, List<KeyBinding> b) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First.Command == p.Second.Command && p.First.Chords.SequenceEqual(p.Second.Chords));
}
