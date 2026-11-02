using System.Text;
using MikePDF.Broker;
using MikePDF.Integration.Tests.Support;
using MikePDF.Protocol;

namespace MikePDF.Integration.Tests;

/// <summary>Dark pages (RenderTile flag bit 2) through the real sandboxed engine and the broker's argument type: the page is recoloured, a colour photograph is not.</summary>
public sealed class DarkPageTests(SandboxFixture sandbox)
{
    private const int Side = 200;

    // A 200 x 200 pt page: a red square at (20,20) and a 16 x 16 colour picture (a photograph by the engine's measure: no white in it) at (110,20), 60 pt square.
    private static byte[] Page()
    {
        var hex = new StringBuilder();
        for (var y = 0; y < 16; y++)
        {
            for (var x = 0; x < 16; x++)
            {
                hex.Append(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{40 + (x * 12):X2}{200 - (y * 10):X2}{60 + (((x + y) * 7) % 150):X2}"));
            }
        }

        var content = $"1 0 0 rg 20 20 60 60 re f\nq 60 0 0 60 110 20 cm\nBI /W 16 /H 16 /CS /RGB /BPC 8 /F /AHx ID\n{hex}>\nEI\nQ\n";
        return TestPdf.Build(Encoding.Latin1.GetBytes(content));
    }

    private static async Task<byte[]> RenderAsync(SupervisedDocument document, uint flags)
    {
        var result = await document.RenderTileAsync(new RenderTileArgs(0, 1f, 0, 0, 0, Side, Side, flags), 1);
        Assert.Equal(Status.Ok, result.Status);
        using var lease = result.Value!;
        var bytes = new byte[Side * Side * 4];
        for (var y = 0; y < Side; y++)
        {
            lease.Pixels.Slice(y * lease.Stride, Side * 4).CopyTo(bytes.AsSpan(y * Side * 4, Side * 4));
        }

        return bytes;
    }

    private static (int R, int G, int B) At(byte[] bgra, int x, int y)
    {
        var i = ((y * Side) + x) * 4;
        return (bgra[i + 2], bgra[i + 1], bgra[i]);
    }

    [Fact]
    public async Task ADarkTileHasDarkPaperAndRecolouredTextButAPhotographKeepsItsColours()
    {
        using var file = new TempFile("dark.pdf", Page());
        await using var supervisor = await sandbox.StartAsync();
        var opened = await supervisor.OpenDocumentAsync(file.Path);
        Assert.Equal(Status.Ok, opened.Status);
        await using var document = opened.Value!;

        var normal = await RenderAsync(document, RenderTileArgs.Annotations);
        var dark = await RenderAsync(document, RenderTileArgs.Annotations | RenderTileArgs.DarkPage);

        Assert.Equal((255, 255, 255), At(normal, 190, 10));
        var paper = At(dark, 190, 10);
        Assert.InRange(paper.R, 17, 19);
        var red = At(dark, 50, 150); // the red square: lighter, same hue
        Assert.True(red.R > 240 && red.G is > 130 and < 160 && red.B is > 130 and < 160, $"red became {red}");

        // The photograph: the pixels of its rectangle (x 110..170, y 120..180) are exactly what the file draws.
        Assert.NotEqual((255, 255, 255), At(normal, 140, 150));
        for (var y = 124; y < 176; y++)
        {
            for (var x = 114; x < 166; x++)
            {
                Assert.Equal(At(normal, x, y), At(dark, x, y));
            }
        }
    }

    [Fact]
    public async Task TheDarkFlagIsPartOfTheRequestSoTilesWithAndWithoutItAreNotMixedUp()
    {
        using var file = new TempFile("dark2.pdf", Page());
        await using var supervisor = await sandbox.StartAsync();
        var opened = await supervisor.OpenDocumentAsync(file.Path);
        await using var document = opened.Value!;
        var a = await RenderAsync(document, RenderTileArgs.Annotations | RenderTileArgs.DarkPage);
        var b = await RenderAsync(document, RenderTileArgs.Annotations);
        var c = await RenderAsync(document, RenderTileArgs.Annotations | RenderTileArgs.DarkPage);
        Assert.NotEqual(a, b);
        Assert.Equal(a, c);
    }
}
