using System.Text.Json;
using System.Text.Json.Serialization;
using MikePDF.Core.Colour;
using MikePDF.Core.Layout;

namespace MikePDF.Core.Persistence;

/// <summary>A document opened recently. Stored on this device only; nothing is sent anywhere (master prompt 14).</summary>
public sealed record RecentFile(string Path, DateTimeOffset LastOpened);

/// <summary>Where a document was left: the page at the top of the window, how far down it, and how it was viewed.</summary>
public sealed record DocumentPosition(string Path, int Page, double PageFraction, double Zoom, FitMode Fit, int ViewTurns, DateTimeOffset Saved);

public sealed class StoreData
{
    public List<RecentFile> Recent { get; set; } = [];

    public List<DocumentPosition> Positions { get; set; } = [];

    /// <summary>The page colour mode, by name. A string rather than the enum, so a value written by another version never makes the whole file unreadable.</summary>
    public string PageColour { get; set; } = nameof(PageColourMode.Normal);
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(StoreData))]
internal sealed partial class StoreJsonContext : JsonSerializerContext;

/// <summary>
/// Recent files, the last position per document and the page colour mode, in one small JSON file next to the app's settings. Written atomically (temp file and
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
    private int _recentLimit = MaxRecent;
    private bool _restorePositions = true;

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

    /// <summary>How many recent files are kept (the setting recent.maxItems: 0 turns the list off, at most 50). Lowering it drops the oldest at once.</summary>
    public int RecentLimit
    {
        get
        {
            lock (_gate)
            {
                return _recentLimit;
            }
        }
        set
        {
            lock (_gate)
            {
                _recentLimit = Math.Clamp(value, 0, 50);
                if (_data.Recent.Count > _recentLimit)
                {
                    _data.Recent = [.. _data.Recent.OrderByDescending(r => r.LastOpened).Take(_recentLimit)];
                    Save();
                }
            }
        }
    }

    /// <summary>Whether a document opens where it was left (the setting view.restorePosition). Off: positions are neither used nor recorded.</summary>
    public bool RestorePositions
    {
        get
        {
            lock (_gate)
            {
                return _restorePositions;
            }
        }
        set
        {
            lock (_gate)
            {
                _restorePositions = value;
            }
        }
    }

    public void AddRecent(string path)
    {
        var full = Normalize(path);
        lock (_gate)
        {
            if (_recentLimit == 0)
            {
                return;
            }

            _data.Recent.RemoveAll(r => Same(r.Path, full));
            _data.Recent.Add(new RecentFile(full, _time.GetUtcNow()));
            _data.Recent = [.. _data.Recent.OrderByDescending(r => r.LastOpened).Take(_recentLimit)];
            Save();
        }
    }

    /// <summary>Forgets where every document was left.</summary>
    public void ClearPositions()
    {
        lock (_gate)
        {
            _data.Positions.Clear();
            Save();
        }
    }

    public int PositionCount
    {
        get
        {
            lock (_gate)
            {
                return _data.Positions.Count;
            }
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

    /// <summary>How pages are coloured, for every document (Normal when nothing valid is stored).</summary>
    public PageColourMode PageColour
    {
        get
        {
            lock (_gate)
            {
                return Enum.TryParse<PageColourMode>(_data.PageColour, ignoreCase: false, out var mode) && Enum.IsDefined(mode) ? mode : PageColourMode.Normal;
            }
        }
    }

    public void SetPageColour(PageColourMode mode)
    {
        lock (_gate)
        {
            var name = Enum.IsDefined(mode) ? mode.ToString() : nameof(PageColourMode.Normal);
            if (_data.PageColour != name)
            {
                _data.PageColour = name;
                Save();
            }
        }
    }

    public DocumentPosition? GetPosition(string path)
    {
        var full = Normalize(path);
        lock (_gate)
        {
            return _restorePositions ? _data.Positions.FirstOrDefault(p => Same(p.Path, full)) : null;
        }
    }

    public void SavePosition(string path, int page, double pageFraction, double zoom, FitMode fit, int viewTurns)
    {
        var full = Normalize(path);
        lock (_gate)
        {
            if (!_restorePositions)
            {
                return;
            }

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
