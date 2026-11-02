using System.Text.Json;

namespace MikePDF.Core.Commands;

/// <summary>
/// The last 20 commands run from the palette or a shortcut, newest first, kept in a small file in the private folder so the palette can offer them. It stays on
/// the device and "Clear usage history" empties it. A file that cannot be read means no history. Thread-safe.
/// </summary>
public sealed class CommandHistory
{
    public const int Capacity = 20;
    public const long MaxFileBytes = 16 * 1024;

    private readonly object _gate = new();
    private readonly string? _path;
    private List<string> _ids = [];

    /// <param name="path">The file, or null for a history kept in memory only.</param>
    public CommandHistory(string? path)
    {
        _path = path;
        _ids = Load(path);
    }

    /// <summary>The command ids, most recent first.</summary>
    public IReadOnlyList<string> Recent
    {
        get
        {
            lock (_gate)
            {
                return [.. _ids];
            }
        }
    }

    public bool IsEmpty
    {
        get
        {
            lock (_gate)
            {
                return _ids.Count == 0;
            }
        }
    }

    /// <summary>1 for the most recent command, up to <see cref="Capacity"/>; 0 for one not in the history.</summary>
    public int Rank(string id)
    {
        lock (_gate)
        {
            var index = _ids.IndexOf(id);
            return index < 0 ? 0 : index + 1;
        }
    }

    public void Record(string id)
    {
        lock (_gate)
        {
            _ids.Remove(id);
            _ids.Insert(0, id);
            if (_ids.Count > Capacity)
            {
                _ids.RemoveRange(Capacity, _ids.Count - Capacity);
            }

            Save();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _ids.Clear();
            Save();
        }
    }

    private static List<string> Load(string? path)
    {
        try
        {
            if (path is null || !File.Exists(path) || new FileInfo(path).Length > MaxFileBytes)
            {
                return [];
            }

            using var document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { MaxDepth = 4 });
            if (!document.RootElement.TryGetProperty("recent", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var ids = new List<string>();
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 and <= 64 } id && CommandDefinition.IdPattern.IsMatch(id) && !ids.Contains(id))
                {
                    ids.Add(id);
                }

                if (ids.Count == Capacity)
                {
                    break;
                }
            }

            return ids;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return [];
        }
    }

    private void Save()
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteStartArray("recent");
                foreach (var id in _ids)
                {
                    writer.WriteStringValue(id);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            AtomicFile.Write(_path, stream.ToArray());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Losing a history entry must never interrupt the person.
        }
    }
}

/// <summary>Writes a file so that a crash leaves either the old file or the new one, never half of one: write a temp file beside it, then move it over.</summary>
public static class AtomicFile
{
    public static void Write(string path, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }
}
