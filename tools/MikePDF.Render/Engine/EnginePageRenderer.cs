using MikePDF.Broker;
using MikePDF.Protocol;
using MikePDF.Render.Imaging;

namespace MikePDF.Render.Engine;

/// <summary>How a page is laid out on the device for one scale: the numbers both the engine and the reference renderer must be given.</summary>
internal readonly record struct PagePlan(float Scale, int Width, int Height)
{
    private const float MinPageSide = 1f;
    private const float MaxPageSide = 14400f;

    /// <summary>
    /// The device size of a page of <paramref name="widthPoints"/> x <paramref name="heightPoints"/> (as the engine reports them, rotation of the
    /// page included) at <paramref name="requestedScale"/> and a further <paramref name="rotation"/> quarter turns, with the arithmetic
    /// MikePDF.Engine.Pdfium uses. A page that would exceed <paramref name="maxPixels"/> gets a smaller scale instead.
    /// </summary>
    public static PagePlan For(float widthPoints, float heightPoints, double requestedScale, int rotation, long maxPixels)
    {
        double width = Clamp(widthPoints);
        double height = Clamp(heightPoints);
        if ((rotation & 1) != 0)
        {
            (width, height) = (height, width);
        }

        var scale = (float)requestedScale;
        var (deviceWidth, deviceHeight) = Device(width, height, scale);
        while ((long)deviceWidth * deviceHeight > maxPixels)
        {
            scale = (float)Math.Min(scale * 0.99, Math.Sqrt(maxPixels / (width * height)) * 0.999);
            (deviceWidth, deviceHeight) = Device(width, height, scale);
        }

        return new PagePlan(scale, deviceWidth, deviceHeight);
    }

    private static float Clamp(float side) => !float.IsFinite(side) || side < MinPageSide ? MinPageSide : Math.Min(side, MaxPageSide);

    private static (int Width, int Height) Device(double width, double height, float scale) =>
        ((int)Math.Max(1, Math.Round(width * scale, MidpointRounding.AwayFromZero)), (int)Math.Max(1, Math.Round(height * scale, MidpointRounding.AwayFromZero)));
}

/// <summary>What asking the engine for one whole page came to.</summary>
internal sealed record PageRender(string Status, string? Detail, BgraImage? Image, int Tiles);

/// <summary>Renders a page through the sandboxed engine by asking for it tile by tile, as the viewer does, and stitching the tiles together.</summary>
internal static class EnginePageRenderer
{
    /// <summary>The largest tile the protocol allows; it fills one slot of the ring exactly.</summary>
    public const int TileSide = 1024;

    private static long _generation;

    public static async Task<PageRender> RenderAsync(SupervisedDocument document, uint page, PagePlan plan, int rotation, TimeSpan tileDeadline, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        var image = BgraImage.Create(plan.Width, plan.Height, 255, 255, 255);
        var tiles = 0;
        var partial = false;
        for (var y = 0; y < plan.Height; y += TileSide)
        {
            for (var x = 0; x < plan.Width; x += TileSide)
            {
                var tileWidth = Math.Min(TileSide, plan.Width - x);
                var tileHeight = Math.Min(TileSide, plan.Height - y);
                var args = new RenderTileArgs(page, plan.Scale, (ushort)rotation, x, y, (uint)tileWidth, (uint)tileHeight);
                var result = await document.RenderTileAsync(args, (ulong)Interlocked.Increment(ref _generation), tileDeadline, cancellationToken).ConfigureAwait(false);
                tiles++;
                if (result.Status is not (Status.Ok or Status.PartiallyRendered) || result.Value is null)
                {
                    result.Value?.Dispose();
                    return new PageRender(result.Status.ToString(), result.Detail, null, tiles);
                }

                using (var tile = result.Value)
                {
                    image.Blit(tile.Pixels, tile.Stride, tile.Width, tile.Height, x, y);
                    partial |= tile.PartiallyRendered;
                }
            }
        }

        return new PageRender(partial ? nameof(Status.PartiallyRendered) : nameof(Status.Ok), null, image, tiles);
    }
}
