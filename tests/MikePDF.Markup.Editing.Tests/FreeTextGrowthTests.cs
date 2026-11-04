#pragma warning disable CA1861 // test data: arrays written inline are the point of these cases

using MikePDF.Markup.Tests.Support;


namespace MikePDF.Markup.Editing.Tests;

/// <summary>FT-01 to FT-04, FT-17 and FT-18: the font metrics, the WinAnsi filter, the one text layout, and box growth.</summary>
public sealed class FreeTextGrowthTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void FT04_ABoxGrowsDownAsItIsDisplayedWhateverThePageRotation(int rotation)
    {
        var samples = new Samples();
        var frame = Samples.Frame(rotation);
        var shown = new RectD(100, 100, 100, 20);
        var box = samples.Factory.FreeTextOnPage(0, frame, shown, "aaa bbb ccc ddd eee fff ggg hhh iii jjj", size: 12);

        var after = frame.ToDisplay(box.Rect);

        Assert.Equal(100, after.X, 6); // the top-left corner stays where it was displayed
        Assert.Equal(100, after.Y, 6);
        Assert.Equal(100, after.Width, 6);
        Assert.True(after.Height > 20);
    }
}
