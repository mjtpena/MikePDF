using MikePDF.Broker;
using MikePDF.Integration.Tests.Support;
using MikePDF.Protocol;

namespace MikePDF.Integration.Tests;

/// <summary>
/// The form layer (RenderTile and RenderBand flag bit 1) through the real sandboxed engine: a filled AcroForm (Fixtures/acroform_filled.pdf: a text field, a checked checkbox, a combo box
/// and a text field with no appearance stream, 400 x 300 pt) drawn with and without the bit. PDFium draws a widget only through its form-fill environment, so without the bit every
/// field is empty: that is what the viewer and the print pipeline showed before this.
/// </summary>
public sealed class FormLayerTests(SandboxFixture sandbox)
{
    private const int Width = 400;
    private const int Height = 300;

    private static readonly (int X, int Y, byte R, byte G, byte B, string Name)[] Widgets =
    [
        (100, 40, 255, 0, 0, "text field"),
        (40, 100, 0, 153, 0, "checkbox"),
        (100, 160, 0, 0, 255, "combo box"),
    ];

    private async Task<(EngineSupervisor Supervisor, SupervisedDocument Document)> OpenAsync()
    {
        var supervisor = await sandbox.StartAsync();
        var opened = await supervisor.OpenDocumentAsync(SandboxFixture.FixturePath("acroform_filled.pdf"));
        Assert.Equal(Status.Ok, opened.Status);
        return (supervisor, opened.Value!);
    }

    private static async Task<byte[]> TileAsync(SupervisedDocument document, uint flags)
    {
        var result = await document.RenderTileAsync(new RenderTileArgs(0, 1f, 0, 0, 0, Width, Height, flags), 1);
        Assert.Equal(Status.Ok, result.Status);
        using var lease = result.Value!;
        return Packed(lease);
    }

    private static async Task<byte[]> BandAsync(SupervisedDocument document, uint flags, uint top = 0, uint height = Height)
    {
        var result = await document.RenderBandAsync(new RenderBandArgs(0, 72, 0, Width, Height, top, height, flags), 1);
        Assert.Equal(Status.Ok, result.Status);
        using var lease = result.Value!;
        return Packed(lease);
    }

    private static byte[] Packed(TileLease lease)
    {
        var bytes = new byte[lease.Width * lease.Height * 4];
        for (var y = 0; y < lease.Height; y++)
        {
            lease.Pixels.Slice(y * lease.Stride, lease.Width * 4).CopyTo(bytes.AsSpan(y * lease.Width * 4, lease.Width * 4));
        }

        return bytes;
    }

    private static (byte R, byte G, byte B) At(byte[] bgra, int x, int y)
    {
        var i = ((y * Width) + x) * 4;
        return (bgra[i + 2], bgra[i + 1], bgra[i]);
    }

    private static bool HasInk(byte[] bgra, int left, int top, int right, int bottom)
    {
        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                if (At(bgra, x, y) != (255, 255, 255))
                {
                    return true;
                }
            }
        }

        return false;
    }

    [Fact]
    public async Task WithoutTheBitTheFieldsAreEmpty()
    {
        var (supervisor, document) = await OpenAsync();
        await using var _ = supervisor;
        await using var __ = document;

        var picture = await TileAsync(document, RenderTileArgs.Annotations);

        foreach (var (x, y, _, _, _, name) in Widgets)
        {
            Assert.True((255, 255, 255) == At(picture, x, y), $"the {name} is drawn without the form layer");
        }

        Assert.False(HasInk(picture, 200, 20, 380, 60));
    }

    [Fact]
    public async Task WithTheBitATileShowsEveryFieldsContents()
    {
        var (supervisor, document) = await OpenAsync();
        await using var _ = supervisor;
        await using var __ = document;

        var picture = await TileAsync(document, RenderTileArgs.Annotations | RenderTileArgs.FormFields);

        foreach (var (x, y, r, g, b, name) in Widgets)
        {
            Assert.True((r, g, b) == At(picture, x, y), $"the {name} is missing or the wrong colour: {At(picture, x, y)}");
        }

        Assert.True(HasInk(picture, 200, 20, 380, 60), "the text field with no appearance stream shows its value");
        Assert.Equal((255, 255, 255), At(picture, 300, 150)); // nothing is drawn where there is no field
    }

    [Fact]
    public async Task WithTheBitABandForPrintingShowsEveryFieldsContentsAndMatchesTheTile()
    {
        var (supervisor, document) = await OpenAsync();
        await using var _ = supervisor;
        await using var __ = document;

        var without = await BandAsync(document, RenderBandArgs.Annotations | RenderBandArgs.Printing);
        var with = await BandAsync(document, RenderBandArgs.Annotations | RenderBandArgs.FormFields | RenderBandArgs.Printing);

        foreach (var (x, y, r, g, b, name) in Widgets)
        {
            Assert.Equal((255, 255, 255), At(without, x, y));
            Assert.True((r, g, b) == At(with, x, y), $"the {name} did not print");
        }

        Assert.True(HasInk(with, 200, 20, 380, 60));
        Assert.False(HasInk(with, 190, 80, 400, 285), "no field highlight, no stray marks");

        // The screen and the printer draw a field the same way: the same rows, the same pixels.
        var tile = await TileAsync(document, RenderTileArgs.Annotations | RenderTileArgs.FormFields);
        var screenBand = await BandAsync(document, RenderBandArgs.Annotations | RenderBandArgs.FormFields);
        Assert.Equal(tile, screenBand);

        // Bands add up to the page: a widget cut by a seam is drawn on both sides of it.
        var stitched = new List<byte>();
        for (uint top = 0; top < Height; top += 70)
        {
            stitched.AddRange(await BandAsync(document, RenderBandArgs.Annotations | RenderBandArgs.FormFields, top, Math.Min(70, Height - top)));
        }

        Assert.Equal(screenBand, [.. stitched]);
    }

    [Fact]
    public async Task TheFormLayerSurvivesAnEngineRestartAndStaysOffForARequestWithoutTheBit()
    {
        var (supervisor, document) = await OpenAsync();
        await using var _ = supervisor;
        await using var __ = document;
        var before = await TileAsync(document, RenderTileArgs.Annotations | RenderTileArgs.FormFields);

        supervisor.CurrentProcess!.Kill(1);
        for (var i = 0; i < 100 && supervisor.RestartCount == 0; i++)
        {
            await Task.Delay(50);
        }

        // The document is reopened in the new engine, which makes its own form-fill environment on the first request.
        var after = Array.Empty<byte>();
        for (var attempt = 0; attempt < 40 && after.Length == 0; attempt++)
        {
            var result = await document.RenderTileAsync(new RenderTileArgs(0, 1f, 0, 0, 0, Width, Height, RenderTileArgs.Annotations | RenderTileArgs.FormFields), 2);
            if (result.Status == Status.Ok)
            {
                using var lease = result.Value!;
                after = Packed(lease);
            }
            else
            {
                await Task.Delay(100);
            }
        }

        Assert.Equal(before, after);
        Assert.Equal((255, 255, 255), At(await TileAsync(document, RenderTileArgs.Annotations), 100, 40));
    }
}
