using System.Text.Json;
using System.Text.Json.Serialization;
using Quire.Core.Layout;

namespace Quire.Core.Persistence;

/// <summary>A document opened recently. Stored on this device only; nothing is sent anywhere (master prompt 14).</summary>
public sealed record RecentFile(string Path, DateTimeOffset LastOpened);

/// <summary>Where a document was left: the page at the top of the window, how far down it, and how it was viewed.</summary>
public sealed record DocumentPosition(string Path, int Page, double PageFraction, double Zoom, FitMode Fit, int ViewTurns, DateTimeOffset Saved);

public sealed class StoreData
{
    public List<RecentFile> Recent { get; set; } = [];

    public List<DocumentPosition> Positions { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(StoreData))]
internal sealed partial class StoreJsonContext : JsonSerializerContext;

/// <summary>
/// Recent files and the last position per document, in one small JSON file next to the app's settings. Written atomically (temp file and
/// move), so a crash cannot leave half a file; a damaged or unreadable file is treated as empty and never stops the app. Thread-safe.
/// </summary>
public sealed class DocumentStore
{
    public const int MaxRecent = 20;
    public const int MaxPositions = 200;

    private readonly object _gate = new();
    private readonly string _path;
    private readonly TimeProvider _time;
    private StoreData _data;

    public DocumentStore(string path, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _path = path;
        _time = time ?? TimeProvider.System;
        _data = Load(path);
    }

    public IReadOnlyList<RecentFile> Recent
    {
        get
        {
            lock (_gate)
            {
                return [.. _data.Recent.OrderByDescending(r => r.LastOpened)];
            }
        }
    }

    public void AddRecent(string path)
    {
        var full = Normalize(path);
        lock (_gate)
        {
            _data.Recent.RemoveAll(r => Same(r.Path, full));
            _data.Recent.Add(new RecentFile(full, _time.GetUtcNow()));
            _data.Recent = [.. _data.Recent.OrderByDescending(r => r.LastOpened).Take(MaxRecent)];
            Save();
        }
    }

    public void RemoveRecent(string path)
    {
        var full = Normalize(path);
        lock (_gate)
        {
            if (_data.Recent.RemoveAll(r => Same(r.Path, full)) > 0)
            {
                Save();
            }
        }
    }

    public void ClearRecent()
    {
        lock (_gate)
        {
            _data.Recent.Clear();
            Save();
        }
    }

    public DocumentPosition? GetPosition(string path)
    {
        var full = Normalize(path);
        lock (_gate)
        {
            return _data.Positions.FirstOrDefault(p => Same(p.Path, full));
        }
    }

    public void SavePosition(string path, int page, double pageFraction, double zoom, FitMode fit, int viewTurns)
    {
        var full = Normalize(path);
        lock (_gate)
        {
            _data.Positions.RemoveAll(p => Same(p.Path, full));
            _data.Positions.Add(new DocumentPosition(full, Math.Max(0, page), Math.Clamp(pageFraction, 0, 1), ZoomMath.Clamp(zoom), fit, viewTurns, _time.GetUtcNow()));
            _data.Positions = [.. _data.Positions.OrderByDescending(p => p.Saved).Take(MaxPositions)];
            Save();
        }
    }

    private static string Normalize(string path) => System.IO.Path.GetFullPath(path);

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static StoreData Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                using var stream = File.OpenRead(path);
                return JsonSerializer.Deserialize(stream, StoreJsonContext.Default.StoreData) ?? new StoreData();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // Treat a damaged file as empty; the next save replaces it.
        }

        return new StoreData();
    }

    private void Save()
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = _path + ".tmp";
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(_data, StoreJsonContext.Default.StoreData));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Losing a recent-files entry must never interrupt reading.
        }
    }
}
