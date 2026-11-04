#pragma warning disable CA1861 // test data: arrays written inline are the point of these cases

using MikePDF.Markup.Tests.Support;

namespace MikePDF.Markup.Tests;

/// <summary>AC-03, AC-04, AC-05, SH-01, SH-02, SH-04, SH-05, SN-04, SN-05, ST-03: the coordinate rules, hit-testing, move and resize, and the shape constants.</summary>
public sealed class ShapeGeometryTests
{
    [Theory]
    [InlineData(0.25, 8, 6)]
    [InlineData(2, 12, 8)]
    [InlineData(36, 48, 36)]
    public void SH01_ArrowHeadsAndEndShapesHaveTheSpecifiedSizes(double width, double arrow, double side)
    {
        Assert.Equal(arrow, ShapeGeometry.ArrowLength(width));
        Assert.Equal(side, ShapeGeometry.EndShapeSide(width));
    }

    [Theory]
    [InlineData(0.25, LineEnding.None, 0)]
    [InlineData(0.25, LineEnding.OpenArrow, 0)]
    [InlineData(0.25, LineEnding.ClosedArrow, 7.188)]
    [InlineData(0.25, LineEnding.Square, 3)]
    [InlineData(0.25, LineEnding.Circle, 3)]
    [InlineData(0.25, LineEnding.Diamond, 4.243)]
    [InlineData(2, LineEnding.ClosedArrow, 10.376)]
    [InlineData(2, LineEnding.Square, 4)]
    [InlineData(2, LineEnding.Diamond, 5.657)]
    [InlineData(36, LineEnding.ClosedArrow, 43.003)]
    [InlineData(36, LineEnding.Circle, 18)]
    [InlineData(36, LineEnding.Diamond, 25.456)]
    public void SH01_TheLineIsShortenedWhereTheHeadSits(double width, LineEnding ending, double shorten)
    {
        var end = ShapeGeometry.End(new PointD(100, 100), new PointD(1, 0), ending, width, Rgb.Red, null);

        Assert.Equal(shorten, end.Shorten, 3);
    }

    [Fact]
    public void SH01_AnArrowHeadOpensTwentyFiveDegreesEitherSideOfTheLineAndReachesTheTip()
    {
        var tip = new PointD(100, 50);
        var (a, b) = ShapeGeometry.ArrowBase(tip, new PointD(1, 0), 2);

        Assert.Equal(12, Geo.Distance(tip, a), 6);
        Assert.Equal(12, Geo.Distance(tip, b), 6);
        var angle = Math.Atan2(a.Y - tip.Y, a.X - tip.X);
        Assert.Equal(180 - 25, Math.Abs(angle) * 180 / Math.PI, 6);
        Assert.Equal(a.Y - tip.Y, -(b.Y - tip.Y), 9); // symmetric about the line
    }

    [Theory]
    [InlineData(0.25, DashStyle.Dashed, new[] { 2.0, 2.0 })]
    [InlineData(2, DashStyle.Dashed, new[] { 8.0, 4.0 })]
    [InlineData(36, DashStyle.Dashed, new[] { 144.0, 72.0 })]
    [InlineData(0.25, DashStyle.Dotted, new[] { 1.0, 1.0 })]
    [InlineData(2, DashStyle.Dotted, new[] { 2.0, 4.0 })]
    [InlineData(36, DashStyle.Dotted, new[] { 36.0, 72.0 })]
    public void SH02_DashAndDotPatternsFollowTheWidthWithTheirMinimums(double width, DashStyle style, double[] expected)
    {
        Assert.Equal(expected, ShapeGeometry.DashArray(style, width));
        Assert.Equal(style == DashStyle.Dotted ? CapStyle.Round : CapStyle.Butt, ShapeGeometry.CapFor(style));
        Assert.Null(ShapeGeometry.DashArray(DashStyle.Solid, width));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    public void SH04_NothingIsDrawnOutsideTheRectangleOfAnyKind(int rotate)
    {
        var samples = new Samples();
        foreach (var a in samples.OfEveryKind().Where(static a => a.ReplyKind == ReplyKind.None && a.Kind is not (MarkupKind.FreeText or MarkupKind.Stamp)))
        {
            var scene = SceneBuilder.Build(a, rotate);
            var allowed = Geo.Inflate(a.Kind == MarkupKind.Note ? NoteGeometry.SavedRect(a.Rect, rotate) : a.Rect, 0.05);
            foreach (var path in scene.Items.OfType<ScenePath>())
            {
                var (l, b, r, t) = ContentStreams.ExactBounds(path);
                Assert.True(l >= allowed.Left && r <= allowed.Right && b >= allowed.Bottom && t <= allowed.Top, $"{a.Kind}: a path reaches ({l:0.###}, {b:0.###}, {r:0.###}, {t:0.###}) beyond {a.Rect}");
            }
        }
    }

    [Theory]
    [InlineData(MarkupKind.Line, LineEnding.Circle, LineEnding.Diamond)]
    [InlineData(MarkupKind.Line, LineEnding.Square, LineEnding.ClosedArrow)]
    [InlineData(MarkupKind.PolyLine, LineEnding.OpenArrow, LineEnding.Circle)]
    public void SH04_TheRectangleOfALineIncludesItsEndShapesAndHalfTheWidth(MarkupKind kind, LineEnding start, LineEnding end)
    {
        var samples = new Samples();
        var a = kind == MarkupKind.Line
            ? samples.Factory.Line(0, new PointD(100, 100), new PointD(200, 150), startEnding: start, endEnding: end, width: 6, interior: Rgb.Yellow)
            : samples.Factory.PolyLine(0, [new PointD(100, 100), new PointD(150, 160), new PointD(220, 120)], start, end, width: 6);

        var scene = SceneBuilder.Build(a);

        foreach (var path in scene.Items.OfType<ScenePath>())
        {
            var (l, b, r, t) = ContentStreams.ExactBounds(path);
            var allowed = Geo.Inflate(a.Rect, 0.05);
            Assert.True(l >= allowed.Left && r <= allowed.Right && b >= allowed.Bottom && t <= allowed.Top);
        }

        Assert.True(a.Rect.Width > 100 + 6); // wider than the bare line because of the shapes at its ends
    }

    [Fact]
    public void SH_ADragShorterThanFourPointsIsNotAShapeAndDuplicatesAreMerged()
    {
        Assert.Equal(3, MarkupFactory.Dedupe([new PointD(0, 0), new PointD(0.001, 0), new PointD(10, 0), new PointD(5, 5), new PointD(0, 0)], closed: true).Count);
        Assert.Equal(1_000, ShapeGeometry.MaxVertices);
        var samples = new Samples();
        Assert.Throws<ArgumentException>(() => samples.Factory.Polygon(0, [new PointD(0, 0), new PointD(1, 1)]));
        Assert.Throws<ArgumentException>(() => samples.Factory.PolyLine(0, [new PointD(0, 0)]));
    }

    [Theory]
    [InlineData(1, 100)]
    [InlineData(1, 10)]
    [InlineData(1, 3)]
    [InlineData(1, 1)]
    [InlineData(3, 1)]
    [InlineData(10, 1)]
    [InlineData(100, 1)]
    [InlineData(2, 3)]
    [InlineData(3, 2)]
    [InlineData(150, 40)]
    public void ST03_AStampIsScaledDownToFitTwoHundredByHundredKeepingItsProportions(double width, double height)
    {
        var (w, h) = MarkupFactory.StampFit(width, height, 200, 100);

        Assert.True(w <= 200 + 1e-9 && h <= 100 + 1e-9);
        Assert.Equal(width / height, w / h, 9);
        Assert.True(w <= width + 1e-9 && h <= height + 1e-9);
    }

    [Fact]
    public void ST03_ADraggedBoxFitsTheStampInsideItAndTheUsersTurnsSwapTheNativeSize()
    {
        var samples = new Samples();
        var drag = new PdfRect(100, 300, 160, 400);

        var stamp = samples.Factory.Stamp(0, "Approved", new PointD(0, 0), drag);
        var turned = samples.Factory.Stamp(0, "Approved", new PointD(0, 0), null, userTurns: 1);

        Assert.True(stamp.Rect.Width <= 60 + 1e-9 && stamp.Rect.Height <= 100 + 1e-9);
        Assert.Equal(StampArtwork.NativeSize("Approved").Width / StampArtwork.NativeSize("Approved").Height, stamp.Rect.Width / stamp.Rect.Height, 6);
        Assert.True(turned.Rect.Height > turned.Rect.Width); // turned a quarter: the box is taller than wide
        Assert.Equal(1, ((StampPayload)turned.Payload).UserTurns);
    }

    [Fact]
    public void ST01_EveryBuiltInStampIsInsideItsBoxHasContrastAndFourteenNamesExist()
    {
        Assert.Equal(14, StampCatalog.Entries.Count);
        Assert.Equal(14, StampCatalog.Names.Distinct().Count());
        foreach (var name in StampCatalog.Names)
        {
            var (w, h) = StampArtwork.NativeSize(name);
            Assert.InRange(h, 30, 50); // "about 40 pt" tall
            var items = StampArtwork.Items(name);
            Assert.True(Rgb.Contrast(StampCatalog.RgbOf(name), Rgb.White) >= 4.5, name);
            foreach (var path in items.OfType<ScenePath>())
            {
                var (l, b, r, t) = ContentStreams.ExactBounds(path);
                Assert.True(l >= 0 && b >= 0 && r <= w && t <= h, $"{name}: outline outside the box");
            }

            var text = items.OfType<SceneText>().Single();
            var width = Fonts.StandardFonts.Measure(text.Text, StampArtwork.FontId, text.Size);
            var cap = Fonts.StandardFonts.CapHeight(StampArtwork.FontId) * text.Size / 1000;
            Assert.True(text.X >= StampArtwork.BorderWidth && text.X + width <= w - StampArtwork.BorderWidth, $"{name}: text outside the box");
            Assert.True(text.Y >= StampArtwork.BorderWidth && text.Y + cap <= h - StampArtwork.BorderWidth, $"{name}: text height outside the box");
        }
    }
}
