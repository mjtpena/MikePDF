#pragma warning disable CA1861 // test data: arrays written inline are the point of these cases

using MikePDF.Core.Geometry;
using MikePDF.Markup.Fonts;
using MikePDF.Markup.Tests.Support;

namespace MikePDF.Markup.Tests;

/// <summary>AC-13, TM-09, FT-05, ST-04: the appearance is the scene, written as operators, drawn the way the specs say.</summary>
public sealed class AppearanceTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void AC13_ThePathsOfTheSceneAreTheOnesInTheContentStreamWithinAHundredthOfAPoint(int rotate)
    {
        var samples = new Samples();
        foreach (var a in samples.OfEveryKind())
        {
            var scene = SceneBuilder.Build(a, rotate);
            var form = AppearanceWriter.Write(scene);

            var fromScene = scene.PathPoints().ToList();
            var fromStream = ContentStreams.PathPoints(form.Content);

            Assert.Equal(fromScene.Count, fromStream.Count);
            for (var i = 0; i < fromScene.Count; i++)
            {
                Assert.True(Math.Abs(fromScene[i].X - fromStream[i].X) <= 0.01 && Math.Abs(fromScene[i].Y - fromStream[i].Y) <= 0.01, $"{a.Kind}: point {i}: {fromScene[i]} against {fromStream[i]}");
            }
        }
    }

    [Fact]
    public void AC13_TheTextOfTheSceneIsTheTextOfTheContentStream()
    {
        var samples = new Samples();
        var free = samples.FreeText();
        var scene = SceneBuilder.Build(free);
        var content = AppearanceWriter.Write(scene).Content;

        var shown = ContentStreams.Parse(content).Where(static o => o.Operator == "Tj").Select(static o => o.Operands[0]).ToList();

        Assert.Equal(scene.Texts().Count(), shown.Count);
        Assert.Equal(scene.Texts().Select(t => WinAnsi.ToLiteral(t.Text)), shown);
        Assert.Contains("/TiBo 14 Tf", content, StringComparison.Ordinal);
        // The positions: every Td is the baseline start of the scene's line.
        var positions = ContentStreams.Parse(content).Where(static o => o.Operator == "Td").Select(static o => (ContentStreams.Number(o.Operands[0]), ContentStreams.Number(o.Operands[1]))).ToList();
        Assert.Equal(scene.Texts().Select(t => (Math.Round(t.X, 4), Math.Round(t.Y, 4))), positions.Select(static p => (Math.Round(p.Item1, 4), Math.Round(p.Item2, 4))));
    }

    // ---- TM-09 ----

    [Fact]
    public void TM09_AHighlightFillsEachQuadAroundItsFourCornersWithTheMarkerColourAndMultiplies()
    {
        var samples = new Samples();
        var a = samples.Highlight();
        var scene = SceneBuilder.Build(a);

        var first = (ScenePath)scene.Items[0];
        var corners = ((TextMarkupPayload)a.Payload).Quads[0];
        Assert.Equal(PaintMode.Fill, first.Paint);
        Assert.Equal(Rgb.Yellow, first.Fill);
        Assert.True(scene.Multiply);
        Assert.Equal(0.8, scene.Opacity);
        var path = first.Segments.Take(4).Select(static s => new PointD(s.X1, s.Y1)).ToList();
        Assert.Equal([corners.TopLeft, corners.TopRight, corners.BottomRight, corners.BottomLeft], path); // TL, TR, BR, BL
        var form = AppearanceWriter.Write(scene);
        Assert.True(form.Multiply);
        Assert.Empty(form.Fonts); // text markup uses no fonts
        Assert.StartsWith("/GS gs\n", form.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void TM09_AnUnderlineSitsOnTheBottomEdgeInsetByHalfTheThicknessAndAStrikeGoesThroughTheMiddle()
    {
        var samples = new Samples();
        var underline = samples.Underline(); // thickness 1.5, quad (72,600)-(272,616)
        var strike = samples.StrikeOut(); // thickness 1, quad (72,580)-(272,596)

        var u = (ScenePath)SceneBuilder.Build(underline).Items[0];
        var s = (ScenePath)SceneBuilder.Build(strike).Items[0];

        Assert.Equal((72.0, 600.75, 272.0, 600.75), (u.Segments[0].X1, u.Segments[0].Y1, u.Segments[1].X1, u.Segments[1].Y1));
        Assert.Equal(1.5, u.StrokeWidth);
        Assert.Equal(CapStyle.Butt, u.Cap);
        Assert.Equal((72.0, 588.0, 272.0, 588.0), (s.Segments[0].X1, s.Segments[0].Y1, s.Segments[1].X1, s.Segments[1].Y1));
        // The rectangle is the union of the quads, grown by half the thickness for line kinds.
        Assert.Equal(new PdfRect(72 - 0.75, 600 - 0.75, 272 + 0.75, 616 + 0.75), underline.Rect);
        Assert.Equal(new PdfRect(72, 684, 272, 716), samples.Highlight().Rect); // a highlight's rectangle is exactly the union of its quads
    }

    [Fact]
    public void TM09_ASquigglyHasThreeBySixteenthsOfAWavelengthAndOnePointOfAmplitudeAtThicknessOne()
    {
        var samples = new Samples();
        var a = samples.Squiggly(); // thickness 1, quad (72,560)-(272,576)
        var points = SceneBuilder.SquigglyPoints(((TextMarkupPayload)a.Payload).Quads[0], 1);

        Assert.Equal(1.5, points[1].X - points[0].X, 9); // half a wavelength of 3 points between turning points
        Assert.Equal(2, Math.Abs(points[1].Y - points[0].Y), 9); // peak to peak: twice the amplitude of 1
        Assert.Equal(272, points[^1].X, 9); // ends exactly at the end of the quad
        Assert.All(points, p => Assert.InRange(p.Y, 560, 576)); // inside the quad
        var path = (ScenePath)SceneBuilder.Build(a).Items[0];
        Assert.Equal((CapStyle.Round, JoinStyle.Miter), (path.Cap, path.Join));
        // Scaled with the thickness.
        var thick = SceneBuilder.SquigglyPoints(((TextMarkupPayload)a.Payload).Quads[0], 2);
        Assert.Equal(3, thick[1].X - thick[0].X, 9);
        Assert.Equal(4, Math.Abs(thick[1].Y - thick[0].Y), 9);
    }

    [Fact]
    public void TM09_AQuadTurnedOnItsOwnKeepsTheUnderlineOnItsBottomEdge()
    {
        // Text at 90 degrees: the "bottom" of the quad is its left edge. The line sits on it, inset toward the top-left to bottom-left direction.
        var quad = new Quad(new PointD(100, 300), new PointD(100, 200), new PointD(116, 300), new PointD(116, 200)); // TL, TR, BL, BR of text running downward
        var samples = new Samples();
        var a = samples.Factory.TextMarkup(MarkupKind.Underline, 0, [quad], thickness: 2);

        var path = (ScenePath)SceneBuilder.Build(a).Items[0];

        var up = Geo.Unit(Geo.Sub(quad.TopLeft, quad.BottomLeft));
        Assert.Equal(quad.BottomLeft.X + (up.X * 1), path.Segments[0].X1, 9);
        Assert.Equal(quad.BottomLeft.Y + (up.Y * 1), path.Segments[0].Y1, 9);
        Assert.Equal(quad.BottomRight.Y + (up.Y * 1), path.Segments[1].Y1, 9);
    }

    // ---- shapes ----

    [Fact]
    public void RectanglesAreInsetByHalfTheWidthSoTheStrokeStaysInsideTheRect()
    {
        var samples = new Samples();
        var a = samples.Factory.Box(MarkupKind.Rectangle, 0, new PdfRect(100, 100, 200, 160), Rgb.Red, 4);

        var path = (ScenePath)SceneBuilder.Build(a).Items[0];

        Assert.Equal((102.0, 102.0), (path.Segments[0].X1, path.Segments[0].Y1));
        Assert.Equal((198.0, 158.0), (path.Segments[2].X1, path.Segments[2].Y1));
        Assert.Equal(PaintMode.Stroke, path.Paint);
        Assert.Null(path.Fill);
    }

    [Fact]
    public void ADashedAndAFilledShapeCarryTheirDashAndFill()
    {
        var samples = new Samples();
        var a = samples.Rectangle(); // dashed, width 3, filled, opacity 0.6

        var scene = SceneBuilder.Build(a);
        var path = (ScenePath)scene.Items[0];
        var content = AppearanceWriter.Write(scene).Content;

        Assert.Equal(PaintMode.FillAndStroke, path.Paint);
        Assert.Equal([12.0, 6.0], path.Dash);
        Assert.Contains("[12 6] 0 d", content, StringComparison.Ordinal);
        Assert.Contains("\nB\n", content, StringComparison.Ordinal);
        Assert.True(scene.TransparencyGroup);
        Assert.Equal(0.6, scene.Opacity);
        Assert.False(SceneBuilder.Build(samples.Ellipse()).TransparencyGroup);
    }

    [Fact]
    public void ALineEndsWhereItsHeadsBeginAndAnArrowIsALineWithAnOpenArrow()
    {
        var samples = new Samples();
        var arrow = samples.Arrow();
        Assert.Equal(MarkupKind.Arrow, arrow.Kind);
        var line = samples.Line();
        Assert.Equal(MarkupKind.Line, line.Kind);

        var scene = SceneBuilder.Build(line);
        var body = (ScenePath)scene.Items[0];

        // The closed arrow at the end shortens the line there; the circle at the start shortens it by half the end shape's side (8 / 2).
        var start = new PointD(body.Segments[0].X1, body.Segments[0].Y1);
        Assert.Equal(4, Geo.Distance(start, new PointD(100, 250)), 6);
        var finish = new PointD(body.Segments[1].X1, body.Segments[1].Y1);
        Assert.True(Geo.Distance(finish, new PointD(220, 280)) > 10);
        Assert.Equal(3, scene.Items.Count); // the line, the circle at its start and the arrow head at its end
        Assert.Equal(PaintMode.Fill, ((ScenePath)scene.Items[2]).Paint);
        Assert.Equal(Rgb.Yellow, ((ScenePath)scene.Items[2]).Fill); // the interior colour fills a closed head
    }

    // ---- notes ----

    [Fact]
    public void EachOfTheSevenIconsDrawsInsideItsRectangleWithTheAnnotationColour()
    {
        var samples = new Samples();
        foreach (var icon in Enum.GetValues<NoteIcon>())
        {
            var rect = new PdfRect(100, 100, 120, 120);
            var items = NoteArtwork.Items(icon, rect, new Rgb(1, 0.8, 0.1));

            Assert.NotEmpty(items);
            Assert.Contains(items, i => i.Fill == new Rgb(1, 0.8, 0.1) || (i.Fill is { } f && f != Rgb.Black)); // the colour is used
            foreach (var path in items)
            {
                var (l, b, r, t) = ContentStreams.ExactBounds(path);
                Assert.True(l >= 99.5 && b >= 99.5 && r <= 120.5 && t <= 120.5, $"{icon}: reaches ({l:0.##}, {b:0.##}, {r:0.##}, {t:0.##})");
            }
        }

        Assert.Equal(7, Enum.GetValues<NoteIcon>().Length);
        Assert.Equal(["Comment", "Note", "Key", "Help", "NewParagraph", "Paragraph", "Insert"], Enum.GetValues<NoteIcon>().Select(NoteArtwork.NameOf));
        Assert.NotNull(samples);
    }

    [Fact]
    public void AReplyAndAReviewStateHaveAnEmptyAppearance()
    {
        var samples = new Samples();
        var note = samples.Note();

        var reply = AppearanceWriter.Write(SceneBuilder.Build(samples.Factory.Reply(note, "yes")));
        var state = AppearanceWriter.Write(SceneBuilder.Build(samples.Factory.ReviewStateChange(note, ReviewState.Rejected)));

        Assert.Equal("/GS gs\n", reply.Content);
        Assert.Equal("/GS gs\n", state.Content);
        Assert.Equal(note.Rect, reply.BBox);
    }

    // ---- FT-05 and ST-04: rotation ----

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

    [Fact]
    public void RotationOutsideRightAnglesIsRefusedAndAMatrixIsOnlyWrittenWhenItIsNotTheIdentity()
    {
        var samples = new Samples();
        Assert.Throws<ArgumentOutOfRangeException>(() => SceneBuilder.Build(samples.Stamp(), 45));
        Assert.True(SceneBuilder.Build(samples.Rectangle()).IsIdentityMatrix);
        Assert.False(SceneBuilder.Build(samples.Stamp()).IsIdentityMatrix); // translated onto the rectangle
    }

    // ---- ink ----

    [Fact]
    public void INK10_AnInkAppearanceFillsOneOutlinePerStrokeAndAHighlighterMultiplies()
    {
        var samples = new Samples();
        var pen = samples.Ink();
        var highlighter = samples.Ink(highlighter: true);

        var penScene = SceneBuilder.Build(pen);
        var highlighterScene = SceneBuilder.Build(highlighter);

        Assert.Equal(2, penScene.Items.Count);
        Assert.All(penScene.Items.Cast<ScenePath>(), static p => Assert.Equal((PaintMode.Fill, null), (p.Paint, p.Stroke)));
        Assert.False(penScene.Multiply);
        Assert.True(highlighterScene.Multiply);
        Assert.Equal(pen.Rect, MarkupGeometry.RectFor(pen));
        var content = AppearanceWriter.Write(penScene).Content;
        Assert.Equal(2, ContentStreams.Parse(content).Count(static o => o.Operator == "f"));
        Assert.True(content.Length < AppearanceForm.MaxContentBytes);
    }

    [Fact]
    public void AnInkAppearanceOverTwoMegabytesIsRefused()
    {
        var samples = new Samples();
        var points = Enumerable.Range(0, 60_000).SelectMany(i => new float[] { (i % 500) * 1f, (float)((i / 500) * 2) + ((i % 2) * 7f) }).ToArray();
        var big = samples.Ink() with { Payload = new InkPayload([new InkStrokeData(points, Enumerable.Repeat(3f, points.Length / 2).ToArray())], false) };

        Assert.ThrowsAny<Exception>(() => AppearanceWriter.Write(SceneBuilder.Build(big)));
    }
}
