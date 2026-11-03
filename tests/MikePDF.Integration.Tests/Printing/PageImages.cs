using MikePDF.Broker;
using MikePDF.Protocol;
using MikePDF.Render.Imaging;

namespace MikePDF.Integration.Tests.Printing;

/// <summary>
/// Reads pages of a document through the engine in bands and reduces them, without ever holding a page at printer resolution. Shared with the UI journeys (tests/MikePDF.UI.Tests links this file), which
/// check the file a print journey wrote with the same comparison the pipeline tests use.
/// </summary>
internal static class PageImages
{
    /// <summary>
    /// Renders page <paramref name="page"/> laid out at <paramref name="widthPx"/> x <paramref name="heightPx"/> (with <paramref name="rotation"/> extra quarter turns) in bands, keeps the
    /// rectangle (<paramref name="cropX"/>, <paramref name="cropY"/>, <paramref name="cropWidth"/>, <paramref name="cropHeight"/>) and averages it in blocks of
    /// <paramref name="factor"/> x <paramref name="factor"/> pixels (print.md PRINT-13 as ADR-0011 decision 5 redefined it: both sides at the printer's resolution, box filtered).
    /// </summary>
    public static async Task<BgraImage> DownscaledAsync(SupervisedDocument document, int page, int widthPx, int heightPx, ushort rotation, uint flags, int cropX, int cropY, int cropWidth, int cropHeight, int factor, int ringSlotBytes = 4 << 20)
    {
        var outWidth = cropWidth / factor;
        var outHeight = cropHeight / factor;
        var sums = new long[outWidth * outHeight * 3];
        var rowsPerBand = Math.Max(factor, (int)Math.Min(16384, ringSlotBytes / (4L * widthPx)) / factor * factor);
        for (var top = cropY; top < cropY + (outHeight * factor); top += rowsPerBand)
        {
            var rows = Math.Min(rowsPerBand, cropY + (outHeight * factor) - top);
            var args = new RenderBandArgs((uint)page, 600, rotation, (uint)widthPx, (uint)heightPx, (uint)top, (uint)rows, flags);
            var result = await document.RenderBandAsync(args, 1, TimeSpan.FromSeconds(60));
            Assert.True(result.IsOk || result.Status == Status.PartiallyRendered, $"RenderBand {result.Status} {result.Detail}");
            using var band = result.Value!;
            var pixels = band.Pixels;
            for (var row = 0; row < rows; row++)
            {
                var by = ((top - cropY) + row) / factor;
                if (by >= outHeight)
                {
                    break;
                }

                var line = pixels.Slice(row * band.Stride, band.Stride);
                for (var bx = 0; bx < outWidth; bx++)
                {
                    var sum = (by * outWidth + bx) * 3;
                    for (var k = 0; k < factor; k++)
                    {
                        var offset = (cropX + (bx * factor) + k) * 4;
                        sums[sum] += line[offset];
                        sums[sum + 1] += line[offset + 1];
                        sums[sum + 2] += line[offset + 2];
                    }
                }
            }
        }

        var image = new byte[outWidth * outHeight * 4];
        var divisor = (long)factor * factor;
        for (var i = 0; i < outWidth * outHeight; i++)
        {
            image[i * 4] = (byte)(sums[i * 3] / divisor);
            image[(i * 4) + 1] = (byte)(sums[(i * 3) + 1] / divisor);
            image[(i * 4) + 2] = (byte)(sums[(i * 3) + 2] / divisor);
            image[(i * 4) + 3] = 255;
        }

        return new BgraImage(outWidth, outHeight, image);
    }
}
