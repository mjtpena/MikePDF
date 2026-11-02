using MikePDF.Core.Colour;

namespace MikePDF.Core.Tests;

/// <summary>
/// The contract between the app's inverted page colour and the engine's dark pages (RenderTile flag bit 2, src/MikePDF.Engine.Pdfium document.cpp, darken_pixels): the engine
/// repeats the matrix in C++, so the numbers it is tested against (dark_page_tests.cpp) are asserted here against the C# matrix, and a change to either side fails one of the two.
/// </summary>
public sealed class DarkPageContractTests
{
    [Theory]
    [InlineData(255, 255, 255, 18, 18, 18)]      // white paper: 7% grey
    [InlineData(0, 0, 0, 242, 242, 242)]         // black text: 95% white
    [InlineData(255, 0, 0, 255, 147, 147)]       // red keeps its hue and gets lighter
    public void TheInvertedMatrixGivesTheNumbersTheEnginesDarkPagesAreTestedAgainst(int r, int g, int b, int er, int eg, int eb)
    {
        var (nr, ng, nb) = PageColourMatrix.For(PageColourMode.Inverted).Apply((byte)r, (byte)g, (byte)b);
        Assert.InRange(nr, er - 1, er + 1);
        Assert.InRange(ng, eg - 1, eg + 1);
        Assert.InRange(nb, eb - 1, eb + 1);
    }

    [Fact]
    public void TheTileRendererAsksTheEngineForDarkPagesOnlyWhenTold()
    {
        var source = File.ReadAllText(Path.Combine(ResourceText.RepoRoot(), "src", "MikePDF.App", "Services", "EngineTileRenderer.cs"));
        Assert.Contains("public bool DarkPages", source, StringComparison.Ordinal);
        Assert.Contains("var dark = DarkPages;", source, StringComparison.Ordinal); // read once, so a tile drawn across a change of setting can be told from the new ones
        Assert.Contains("(dark ? RenderTileArgs.DarkPage : 0)", source, StringComparison.Ordinal);
    }
}
