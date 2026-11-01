using Quire.Core.Geometry;

namespace Quire.Core.Tests;

/// <summary>Coordinate maths of master prompt 6.3: every conversion for each of the four page rotations and for non-zero CropBox origins.</summary>
public sealed class GeometryTests
{
    private static readonly PdfRect Media = new(0, 0, 612, 792);

    public static TheoryData<int, PdfRect?> Pages => new()
    {
        { 0, null },
        { 90, null },
        { 180, null },
        { 270, null },
        { 0, new PdfRect(50, 100, 350, 500) },
        { 90, new PdfRect(50, 100, 350, 500) },
        { 180, new PdfRect(50, 100, 350, 500) },
        { 270, new PdfRect(50, 100, 350, 500) },
        { 450, new PdfRect(50, 100, 350, 500) }, // 450 = 90
        { -90, new PdfRect(10, 20, 600, 780) },   // -90 = 270
    };

    [Theory]
    [MemberData(nameof(Pages))]
    public void TheFourCornersLandWhereRotationPutsThem(int rotate, PdfRect? crop)
    {
        var g = new PageGeometry(Media, crop, rotate);
        var box = g.Box;
        double w = box.Width;
        double h = box.Height;
        var turns = ((rotate / 90 % 4) + 4) % 4;

        var bottomLeft = g.PdfToPage(new PointD(box.Left, box.Bottom));
        var topLeft = g.PdfToPage(new PointD(box.Left, box.Top));
        var topRight = g.PdfToPage(new PointD(box.Right, box.Top));
        var bottomRight = g.PdfToPage(new PointD(box.Right, box.Bottom));

        // Page size after /Rotate.
        var (pw, ph) = turns % 2 == 0 ? (w, h) : (h, w);
        Assert.Equal(pw, g.Size.Width);
        Assert.Equal(ph, g.Size.Height);

        // Unrotated: bottom-left of the box is bottom-left of the page. A clockwise turn moves each corner one place clockwise.
        PointD[] clockwiseFromTopLeft = [new(0, 0), new(pw, 0), new(pw, ph), new(0, ph)]; // TL, TR, BR, BL of the displayed page
        // Index of each PDF corner among the displayed corners when turns = 0: TL=0, TR=1, BR=2, BL=3.
        Assert.Equal(clockwiseFromTopLeft[turns % 4], topLeft);
        Assert.Equal(clockwiseFromTopLeft[(1 + turns) % 4], topRight);
        Assert.Equal(clockwiseFromTopLeft[(2 + turns) % 4], bottomRight);
        Assert.Equal(clockwiseFromTopLeft[(3 + turns) % 4], bottomLeft);
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public void PdfToPageAndBackIsTheIdentity(int rotate, PdfRect? crop)
    {
        var g = new PageGeometry(Media, crop, rotate);
        foreach (var (x, y) in new[] { (g.Box.Left, g.Box.Bottom), (g.Box.Right, g.Box.Top), (g.Box.Left + 13.37, g.Box.Top - 91.5), (g.Box.Left + (g.Box.Width / 2), g.Box.Bottom + (g.Box.Height / 3)) })
        {
            var round = g.PageToPdf(g.PdfToPage(new PointD(x, y)));
            Assert.Equal(x, round.X, 9);
            Assert.Equal(y, round.Y, 9);
        }
    }

    [Fact]
    public void ACropBoxOriginMovesNothingRelativeToThePage()
    {
        var plain = new PageGeometry(Media, null, 0);
        var cropped = new PageGeometry(Media, new PdfRect(100, 200, 400, 600), 0);

        // The page origin is the crop box's top-left, not the media box's.
        Assert.Equal(new PointD(0, 0), cropped.PdfToPage(new PointD(100, 600)));
        Assert.Equal(new PointD(300, 400), cropped.PdfToPage(new PointD(400, 200)));
        Assert.Equal(new PointD(100, 792 - 600), plain.PdfToPage(new PointD(100, 600)));
        Assert.Equal(new SizeD(300, 400), cropped.Size);
    }

    [Fact]
    public void ACropBoxOutsideTheMediaBoxIsClippedAndAnEmptyOneIgnored()
    {
        var clipped = new PageGeometry(Media, new PdfRect(-50, -50, 700, 400), 0);
        Assert.Equal(new PdfRect(0, 0, 612, 400), clipped.Box);

        var ignored = new PageGeometry(Media, new PdfRect(1000, 1000, 1100, 1100), 0);
        Assert.Equal(Media, ignored.Box);
    }

    [Fact]
    public void RectanglesConvertToTheirBoundingBoxInEveryRotation()
    {
        foreach (var rotate in new[] { 0, 90, 180, 270 })
        {
            var g = new PageGeometry(Media, new PdfRect(50, 100, 350, 500), rotate);
            var rect = new PdfRect(100, 150, 200, 250);
            var page = g.PdfToPage(rect);
            Assert.Equal(100, rotate % 180 == 0 ? page.Width : page.Height, 9);
            Assert.Equal(100, rotate % 180 == 0 ? page.Height : page.Width, 9);
            var back = g.PageToPdf(page);
            Assert.Equal(rect.Left, back.Left, 9);
            Assert.Equal(rect.Top, back.Top, 9);
            Assert.Equal(rect.Right, back.Right, 9);
            Assert.Equal(rect.Bottom, back.Bottom, 9);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ViewRotationTurnsThePageWithoutTouchingTheFile(int viewTurns)
    {
        var g = new PageGeometry(Media, null, 0);
        var t = new PageTransform(g, viewTurns, 2.0);

        Assert.Equal(viewTurns % 2 == 0 ? 1224 : 1584, t.PixelWidth);
        Assert.Equal(viewTurns % 2 == 0 ? 1584 : 1224, t.PixelHeight);

        // The page's top-left corner goes one place clockwise per turn.
        var pageTopLeft = new PointD(0, 0);
        var device = t.PageToDevice(pageTopLeft);
        PointD[] corners = [new(0, 0), new(t.PixelWidth, 0), new(t.PixelWidth, t.PixelHeight), new(0, t.PixelHeight)];
        Assert.Equal(corners[viewTurns].X, device.X, 6);
        Assert.Equal(corners[viewTurns].Y, device.Y, 6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void DeviceToPdfInvertsPdfToDeviceForEveryPageAndViewRotation(int rotate)
    {
        for (var view = 0; view < 4; view++)
        {
            var g = new PageGeometry(Media, new PdfRect(50, 100, 350, 500), rotate);
            var t = new PageTransform(g, view, 1.7);
            var pdf = new PointD(123.4, 321.0);
            var back = t.DeviceToPdf(t.PdfToDevice(pdf));
            Assert.Equal(pdf.X, back.X, 9);
            Assert.Equal(pdf.Y, back.Y, 9);
        }
    }

    [Fact]
    public void ThePixelSizeRoundsLikeTheEngineAndTheEffectiveScaleFollowsIt()
    {
        // llround(612.25 * 1.0) = 612, llround(200.5) = 201 (halves away from zero).
        Assert.Equal(3, PageTransform.EngineRounding(2.5));
        Assert.Equal(-3, PageTransform.EngineRounding(-2.5));
        Assert.Equal(201, PageTransform.EngineRounding(200.5));

        var t = new PageTransform(new SizeD(200.5, 100), 0, 1.0);
        Assert.Equal(201, t.PixelWidth);
        Assert.Equal(201 / 200.5, t.ScaleX, 12);
        Assert.Equal(1.0, t.ScaleY, 12);
        var corner = t.PageToDevice(new PointD(200.5, 100));
        Assert.Equal(201, corner.X, 9);
        Assert.Equal(100, corner.Y, 9);
    }

    [Fact]
    public void ATransformWithoutBoxesCannotConvertPdfSpace()
    {
        var t = new PageTransform(new SizeD(100, 100), 0, 1.0);
        Assert.Throws<InvalidOperationException>(() => t.PdfToDevice(new PointD(1, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PageTransform(new SizeD(100, 100), 0, 0));
    }

    [Fact]
    public void TurnsNormaliseAndRotateSizes()
    {
        Assert.Equal(3, QuarterTurns.Normalize(-1));
        Assert.Equal(1, QuarterTurns.Normalize(5));
        Assert.Equal(1, QuarterTurns.FromDegrees(450));
        Assert.True(QuarterTurns.IsOdd(3));
        Assert.Equal(new SizeD(2, 1), QuarterTurns.Rotate(new SizeD(1, 2), 1));
        Assert.Equal(new SizeD(1, 2), QuarterTurns.Rotate(new SizeD(1, 2), 2));
    }

    [Fact]
    public void RectanglesIntersectAndInflate()
    {
        var a = new RectD(0, 0, 10, 10);
        Assert.True(a.Intersects(new RectD(5, 5, 10, 10)));
        Assert.False(a.Intersects(new RectD(10, 0, 5, 5)));
        Assert.Equal(new RectD(5, 5, 5, 5), a.Intersect(new RectD(5, 5, 10, 10)));
        Assert.True(a.Intersect(new RectD(20, 20, 1, 1)).IsEmpty);
        Assert.Equal(new RectD(-1, -1, 12, 12), a.Inflate(1));
        Assert.Equal(new RectD(2, 3, 10, 10), a.Offset(2, 3));
        Assert.True(new SizeD(0, 5).IsEmpty);
    }
}
