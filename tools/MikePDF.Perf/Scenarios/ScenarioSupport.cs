using MikePDF.Broker;
using MikePDF.Core.Geometry;
using MikePDF.Core.Tiles;
using MikePDF.Perf.Documents;
using MikePDF.Perf.Setup;
using MikePDF.Protocol;

namespace MikePDF.Perf.Scenarios;

/// <summary>How much to measure. A metric's reported value is the median of <see cref="Iterations"/> (or <see cref="HeavyIterations"/>) timed runs after <see cref="Warmup"/> untimed ones.</summary>
public sealed record PerfProfile(string Name, int Warmup, int Iterations, int HeavyIterations, int QuietWaitSeconds = 0)
{
    /// <summary>For tests: enough to prove every scenario runs and its checks hold; the numbers mean nothing.</summary>
    public static PerfProfile Quick { get; } = new("quick", 1, 3, 1);

    /// <summary>CI: median of 15 (5 for the heavy scenarios), after 3 warm-ups.</summary>
    public static PerfProfile Ci { get; } = new("ci", 3, 15, 5, 20);

    /// <summary>A reference-hardware or baseline run: median of 30 (9 heavy).</summary>
    public static PerfProfile Full { get; } = new("full", 5, 30, 9, 40);

    public static PerfProfile? Find(string name) => name.ToUpperInvariant() switch
    {
        "QUICK" => Quick,
        "CI" => Ci,
        "FULL" => Full,
        _ => null,
    };
}

public sealed class PerfContext
{
    public required EngineWorkspace Engine { get; init; }

    public required IReadOnlyDictionary<string, GeneratedDocument> Documents { get; init; }

    public required PerfProfile Profile { get; init; }

    public Action<string> Log { get; init; } = _ => { };

    /// <summary>An explicit MSIX to measure; null to look in build/out/package.</summary>
    public string? PackagePath { get; init; }

    private int _unexpectedCrashes;

    /// <summary>Engines that died on their own while a scenario was running (it is not counted when a scenario kills one on purpose). Each such iteration was repeated.</summary>
    public int UnexpectedEngineCrashes => Volatile.Read(ref _unexpectedCrashes);

    /// <summary>The most one iteration is repeated after the engine dies under it; the next death is a failure of the metric.</summary>
    public const int MaxCrashRepeats = 5;

    /// <summary>How long to wait before repeating an iteration, times the number of the repeat: whatever killed the engine (a starved machine, another program) may still be at it.</summary>
    public TimeSpan CrashBackoff { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Runs one iteration. If the sandboxed engine dies on its own while it runs (which it must not), the death is counted and logged, the iteration is thrown
    /// away and run again, up to <see cref="MaxCrashRepeats"/> times in a row: a measurement is not worth failing a build over, but the count goes into the
    /// result file and the report, because an engine that dies by itself is a defect to be found, not noise to be hidden.
    /// </summary>
    public async Task<T> Resilient<T>(Func<Task<T>> iteration, string what)
    {
        ArgumentNullException.ThrowIfNull(iteration);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await iteration();
            }
            catch (Exception e) when (e is EngineCrashedException or EngineStartException)
            {
                Interlocked.Increment(ref _unexpectedCrashes);
                Log($"{what}: the engine died during an iteration ({e.Message}); repeating it ({attempt} of {MaxCrashRepeats})");
                if (attempt >= MaxCrashRepeats)
                {
                    throw;
                }

                await Task.Delay(CrashBackoff * attempt);
            }
        }
    }

    public GeneratedDocument Document(DocumentSpec spec) =>
        Documents.TryGetValue(spec.Id, out var document) ? document : throw new PerfException($"The document {spec.Id} was not generated.");
}

/// <summary>A scenario could not produce a trustworthy number: the engine failed, or what it returned was not what the document must give.</summary>
public class PerfException : Exception
{
    public PerfException(string message)
        : base(message)
    {
    }

    public PerfException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>The engine process died under a request (status EngineCrashed).</summary>
public sealed class EngineCrashedException(string message) : PerfException(message);

internal static class Checks
{
    public static T Require<T>(EngineResult<T> result, string what)
        where T : class
    {
        if (result is { Status: Status.Ok, Value: { } value })
        {
            return value;
        }

        var message = $"{what} failed: {result.Status} {result.Detail}";
        throw result.Status == Status.EngineCrashed ? new EngineCrashedException(message) : new PerfException(message);
    }
}

/// <summary>The render parameters the app uses at 100% zoom on a 96 dpi display, so that a tile here is a tile there.</summary>
internal static class AppTiles
{
    /// <summary>96 / 72: device pixels per point at 100%.</summary>
    public const float Scale = 96f / 72f;

    /// <summary>The low-res placeholder box the app's planner uses.</summary>
    public static (float Scale, uint Width, uint Height) Placeholder(double widthPt, double heightPt)
    {
        var (scale, width, height) = TilePlanner.LowResSize(new SizeD(widthPt, heightPt), new TilePlannerOptions());
        return (scale, (uint)width, (uint)height);
    }

    /// <summary>The first tile of a page at 100%: tile (0, 0), clipped to the page.</summary>
    public static RenderTileArgs First(uint page, float widthPt, float heightPt)
    {
        var rect = TileGrid.Rect(0, 0, PageTransform.EngineRounding(widthPt * Scale), PageTransform.EngineRounding(heightPt * Scale));
        return new RenderTileArgs(page, Scale, 0, 0, 0, (uint)rect.Width, (uint)rect.Height);
    }

    /// <summary>Every tile of a page at 100%, in rows. TileX and TileY of a request are the tile's pixel offset in the scaled page.</summary>
    public static IEnumerable<RenderTileArgs> All(uint page, float widthPt, float heightPt)
    {
        var widthPx = PageTransform.EngineRounding(widthPt * Scale);
        var heightPx = PageTransform.EngineRounding(heightPt * Scale);
        for (var ty = 0; ty < TileGrid.Rows(heightPx); ty++)
        {
            for (var tx = 0; tx < TileGrid.Columns(widthPx); tx++)
            {
                var rect = TileGrid.Rect(tx, ty, widthPx, heightPx);
                yield return new RenderTileArgs(page, Scale, 0, rect.X, rect.Y, (uint)rect.Width, (uint)rect.Height); // the protocol takes pixel offsets, not tile indexes
            }
        }
    }

    /// <summary>True when the tile has any pixel that is not white: a page of text must.</summary>
    public static bool HasInk(TileLease tile)
    {
        var pixels = tile.Pixels;
        for (var y = 0; y < tile.Height; y++)
        {
            var row = pixels.Slice(y * tile.Stride, tile.Width * 4);
            for (var x = 0; x < row.Length; x += 4)
            {
                if (row[x] != 0xFF || row[x + 1] != 0xFF || row[x + 2] != 0xFF)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>The sizes (points) of every page, read the way the app reads them: in chunks.</summary>
    public static async Task<(float Width, float Height)[]> ReadAllSizesAsync(SupervisedDocument document, int chunk = 128)
    {
        var sizes = new (float, float)[document.PageCount];
        for (uint first = 0; first < document.PageCount; first += (uint)chunk)
        {
            var count = Math.Min((uint)chunk, document.PageCount - first);
            var reply = Checks.Require(await document.GetPageSizesAsync(first, count), "GetPageSizes");
            if (reply.Widths.Length != count || reply.Heights.Length != count)
            {
                throw new PerfException("The engine returned the wrong number of page sizes.");
            }

            for (var i = 0; i < count; i++)
            {
                sizes[first + i] = (reply.Widths[i], reply.Heights[i]);
            }
        }

        return sizes;
    }
}
