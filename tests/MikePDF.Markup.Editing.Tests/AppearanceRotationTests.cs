#pragma warning disable CA1861 // test data: arrays written inline are the point of these cases

using MikePDF.Markup.Tests.Support;


namespace MikePDF.Markup.Editing.Tests;

/// <summary>AC-13, TM-09, FT-05, ST-04: the appearance is the scene, written as operators, drawn the way the specs say.</summary>
public sealed class AppearanceRotationTests
{
    [Theory]
    [InlineData(0, 1, 0, 0, 1)]
    [InlineData(90, 0, 1, -1, 0)]
    [InlineData(180, -1, 0, 0, -1)]
    [InlineData(270, 0, -1, 1, 0)]
    public void FT05_ATextBoxOnATurnedPageIsTurnedBackByTheFormMatrix(int pageRotation, double a, double b, double c, double d)
    {
        var samples = new Samples();
        var frame = Samples.Frame(pageRotation);
        var box = samples.Factory.FreeTextOnPage(0, frame, new RectD(100, 100, 200, 40), "Hello", size: 12);

        var scene = SceneBuilder.Build(box, pageRotation);

        Assert.Equal([a, b, c, d], scene.Matrix.Take(4));
        // The turned form's bounding box lands on the rectangle.
        var m = scene.Matrix;
        var corners = new[] { (scene.BBox.Left, scene.BBox.Bottom), (scene.BBox.Right, scene.BBox.Bottom), (scene.BBox.Left, scene.BBox.Top), (scene.BBox.Right, scene.BBox.Top) }
            .Select(p => new PointD((m[0] * p.Item1) + (m[2] * p.Item2) + m[4], (m[1] * p.Item1) + (m[3] * p.Item2) + m[5])).ToList();
        var landed = Geo.BoundsOf(corners);
        Assert.True(Geo.NearlyEqual(landed, box.Rect, 1e-6), $"{landed} against {box.Rect}");
        // On a page that is not turned there is nothing to undo: the content is in the page's own coordinates.
        if (pageRotation == 0)
        {
            Assert.True(scene.IsIdentityMatrix);
            Assert.Equal(box.Rect, scene.BBox);
        }
    }

    [Fact]
    public void FT05_TheTextRunsAlongTheDisplayedXAxisOnATurnedPage()
    {
        // On a page turned 90 degrees clockwise the displayed "right" is user "up": the baseline of the unrotated form must map to user-space vertical.
        var samples = new Samples();
        var frame = Samples.Frame(90);
        var box = samples.Factory.FreeTextOnPage(0, frame, new RectD(100, 100, 200, 40), "Hello", size: 12);
        var scene = SceneBuilder.Build(box, 90);
        var m = scene.Matrix;

        // The form's x axis maps to the user vector (m0, m1) = (0, 1): up. A reader draws the page turned clockwise, which makes up point right: upright text.
        Assert.Equal((0, 1), (m[0], m[1]));
        Assert.Equal(1, Math.Abs(m[0] * m[3]) + Math.Abs(m[1] * m[2]));
        var text = scene.Texts().First();
        Assert.True(text.X >= 0 && text.Y >= 0 && text.X <= scene.BBox.Right && text.Y <= scene.BBox.Top);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(0, 1, 270)]
    [InlineData(0, 2, 180)]
    [InlineData(0, 3, 90)]
    [InlineData(90, 0, 90)]
    [InlineData(90, 1, 0)]
    [InlineData(90, 2, 270)]
    [InlineData(90, 3, 180)]
    [InlineData(180, 0, 180)]
    [InlineData(180, 1, 90)]
    [InlineData(180, 2, 0)]
    [InlineData(180, 3, 270)]
    [InlineData(270, 0, 270)]
    [InlineData(270, 1, 180)]
    [InlineData(270, 2, 90)]
    [InlineData(270, 3, 0)]
    public void ST04_TheStampsFormIsTurnedBackByThePageRotationAndForwardByTheUsersTurns(int pageRotation, int turns, int counterClockwiseDegrees)
    {
        var samples = new Samples();
        var frame = Samples.Frame(pageRotation);
        var stamp = samples.Factory.StampOnPage(0, frame, "Approved", new PointD(100, 100), userTurns: turns);

        var scene = SceneBuilder.Build(stamp, pageRotation);

        var (cos, sin) = counterClockwiseDegrees switch { 90 => (0.0, 1.0), 180 => (-1.0, 0.0), 270 => (0.0, -1.0), _ => (1.0, 0.0) };
        Assert.Equal([cos, sin, -sin, cos], scene.Matrix.Take(4));
        var (w, h) = StampArtwork.NativeSize("Approved");
        Assert.Equal(new PdfRect(0, 0, w, h), scene.BBox); // native size: the reader scales it onto the rectangle
        // The rectangle is the enclosing box of the turned form: its proportions are the native ones, swapped for an odd quarter.
        var odd = counterClockwiseDegrees % 180 != 0;
        Assert.Equal(odd ? h / w : w / h, stamp.Rect.Width / stamp.Rect.Height, 6);
        // And as displayed it has the proportions of the stamp turned by the user's quarters only.
        var shown = frame.ToDisplay(stamp.Rect);
        Assert.Equal(turns % 2 == 0 ? w / h : h / w, shown.Width / shown.Height, 6);
    }
}
