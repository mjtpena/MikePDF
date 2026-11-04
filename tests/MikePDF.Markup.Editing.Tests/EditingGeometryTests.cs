#pragma warning disable CA1861 // test data: arrays written inline are the point of these cases

using System.Diagnostics;
using MikePDF.Markup.Tests.Support;


namespace MikePDF.Markup.Editing.Tests;

/// <summary>AC-03, AC-04, AC-05, SH-01, SH-02, SH-04, SH-05, SN-04, SN-05, ST-03: the coordinate rules, hit-testing, move and resize, and the shape constants.</summary>
public sealed class EditingGeometryTests
{
    public static TheoryData<int, bool> Pages => new()
    {
        { 0, false }, { 90, false }, { 180, false }, { 270, false },
        { 0, true }, { 90, true }, { 180, true }, { 270, true },
    };

    [Theory]
    [MemberData(nameof(Pages))]
    public void AC03_PointsAndRectanglesRoundTripBetweenTheDisplayedPageAndUserSpace(int rotate, bool cropOrigin)
    {
        var frame = Samples.Frame(rotate, cropOrigin ? [50, 30, 562, 762] : null);
        var size = frame.DisplaySize;

        foreach (var (fx, fy) in new[] { (0.0, 0.0), (1.0, 0.0), (0.0, 1.0), (1.0, 1.0), (0.37, 0.81) })
        {
            var shown = new PointD(fx * size.Width, fy * size.Height);
            var back = frame.ToDisplay(frame.ToUser(shown));
            Assert.True(Math.Abs(back.X - shown.X) < 0.01 && Math.Abs(back.Y - shown.Y) < 0.01, $"{shown} came back as {back}");
        }

        var rect = new RectD(40, 55, 120, 30);
        var userRect = frame.ToUser(rect);
        var shownAgain = frame.ToDisplay(userRect);
        Assert.True(Math.Abs(shownAgain.X - rect.X) < 0.01 && Math.Abs(shownAgain.Y - rect.Y) < 0.01 && Math.Abs(shownAgain.Width - rect.Width) < 0.01 && Math.Abs(shownAgain.Height - rect.Height) < 0.01);
        Assert.True(userRect.Left >= frame.Box.Left && userRect.Right <= frame.Box.Right && userRect.Bottom >= frame.Box.Bottom && userRect.Top <= frame.Box.Top);
    }

    [Theory]
    [InlineData(0, 50, 762)] // the top-left of the page is the top-left of the CropBox
    [InlineData(90, 50, 30)] // turned a quarter clockwise, the top-left is the old bottom-left
    [InlineData(180, 562, 30)]
    [InlineData(270, 562, 762)]
    public void AC03_TheTopLeftOfTheDisplayedPageIsTheRightCornerOfTheCropBox(int rotate, double x, double y)
    {
        var corner = Samples.Frame(rotate, [50, 30, 562, 762]).ToUser(new PointD(0, 0));

        Assert.Equal((x, y), (Math.Round(corner.X, 6), Math.Round(corner.Y, 6)));
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public void SN04_ANoteIsTwentyByTwentyWithItsTopLeftAtTheClickAndInsideThePage(int rotate, bool cropOrigin)
    {
        var frame = Samples.Frame(rotate, cropOrigin ? [50, 30, 562, 762] : null);
        var click = new PointD(100, 140);

        var rect = NotePlacement.Place(frame, click);

        Assert.Equal((20, 20), (Math.Round(rect.Width, 6), Math.Round(rect.Height, 6)));
        var shown = frame.ToDisplay(rect);
        Assert.Equal((100, 140), (Math.Round(shown.X, 6), Math.Round(shown.Y, 6)));
        Assert.True(rect.Left >= frame.Box.Left - 1e-9 && rect.Right <= frame.Box.Right + 1e-9 && rect.Bottom >= frame.Box.Bottom - 1e-9 && rect.Top <= frame.Box.Top + 1e-9);

        // The rectangle the file holds is 20 by 20 and inside the page too, and the displayed icon is what the saved rectangle says it is.
        var saved = NoteGeometry.SavedRect(rect, rotate);
        Assert.Equal((20, 20), (Math.Round(saved.Width, 6), Math.Round(saved.Height, 6)));
        Assert.True(saved.Left >= frame.Box.Left - 1e-9 && saved.Right <= frame.Box.Right + 1e-9 && saved.Bottom >= frame.Box.Bottom - 1e-9 && saved.Top <= frame.Box.Top + 1e-9);
        Assert.True(Geo.NearlyEqual(rect, NoteGeometry.FromSavedRect(saved, rotate), 1e-9));

        var pulledIn = NotePlacement.Place(frame, new PointD(-50, 10_000));
        var pulledSaved = NoteGeometry.SavedRect(pulledIn, rotate);
        Assert.True(pulledSaved.Left >= frame.Box.Left - 1e-9 && pulledSaved.Right <= frame.Box.Right + 1e-9 && pulledSaved.Bottom >= frame.Box.Bottom - 1e-9 && pulledSaved.Top <= frame.Box.Top + 1e-9);
        Assert.True(pulledIn.Left >= frame.Box.Left - 1e-9 && pulledIn.Top <= frame.Box.Top + 1e-9 && pulledIn.Right <= frame.Box.Right + 1e-9 && pulledIn.Bottom >= frame.Box.Bottom - 1e-9);
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public void SN05_ThePopupIs180ByHundredToTheRightOfTheIconAndStaysInsideThePage(int rotate, bool cropOrigin)
    {
        var frame = Samples.Frame(rotate, cropOrigin ? [50, 30, 562, 762] : null);
        var icon = NotePlacement.Place(frame, new PointD(60, 80));

        var popup = frame.ToDisplay(NotePlacement.PopupFor(frame, icon));

        Assert.Equal((180, 100), (Math.Round(popup.Width, 6), Math.Round(popup.Height, 6)));
        Assert.True(popup.X >= frame.ToDisplay(icon).Right);
        Assert.True(popup.X >= 0 && popup.Y >= 0 && popup.Right <= frame.DisplaySize.Width + 1e-6 && popup.Bottom <= frame.DisplaySize.Height + 1e-6);

        // Near the right and bottom edges it is moved back inside.
        var corner = NotePlacement.Place(frame, new PointD(frame.DisplaySize.Width - 25, frame.DisplaySize.Height - 25));
        var moved = frame.ToDisplay(NotePlacement.PopupFor(frame, corner));
        Assert.True(moved.Right <= frame.DisplaySize.Width + 1e-6 && moved.Bottom <= frame.DisplaySize.Height + 1e-6 && moved.X >= 0 && moved.Y >= 0);
    }

    private static (AnnotationStore Store, Samples Samples) StoreOf(params Func<Samples, Annotation>[] makers)
    {
        var samples = new Samples();
        var store = new AnnotationStore();
        foreach (var make in makers)
        {
            store.Add(make(samples));
        }

        return (store, samples);
    }

    [Fact]
    public void AC04_TheTopmostOfOverlappingAnnotationsWins()
    {
        var samples = new Samples();
        var store = new AnnotationStore();
        var below = samples.Factory.Box(MarkupKind.Rectangle, 0, new PdfRect(100, 100, 200, 200), fill: Rgb.White);
        var above = samples.Factory.Box(MarkupKind.Rectangle, 0, new PdfRect(150, 150, 250, 250), fill: Rgb.White);
        store.Add(below);
        store.Add(above);

        Assert.Equal(above.Id, HitTester.HitTest(store, 0, new PointD(175, 175), 3)!.Annotation.Id);
        Assert.Equal(below.Id, HitTester.HitTest(store, 0, new PointD(120, 120), 3)!.Annotation.Id);
        Assert.Equal([above.Id, below.Id], HitTester.HitAll(store, 0, new PointD(175, 175), 3).Select(static h => h.Annotation.Id));
        Assert.Null(HitTester.HitTest(store, 0, new PointD(400, 400), 3));
        Assert.Null(HitTester.HitTest(store, 1, new PointD(175, 175), 3)); // another page
    }

    [Fact]
    public void AC04_FilledShapesHitInsideAndUnfilledOnesOnlyOnTheirStroke()
    {
        var (store, samples) = StoreOf(
            s => s.Factory.Box(MarkupKind.Rectangle, 0, new PdfRect(100, 100, 200, 200), Rgb.Red, 2, fill: Rgb.White),
            s => s.Factory.Box(MarkupKind.Rectangle, 0, new PdfRect(300, 100, 400, 200), Rgb.Red, 2),
            s => s.Factory.Box(MarkupKind.Ellipse, 0, new PdfRect(100, 300, 200, 400), Rgb.Red, 2),
            s => s.Factory.Polygon(0, [new PointD(300, 300), new PointD(400, 300), new PointD(350, 400)], Rgb.Red, 2));
        var ids = store.OnPage(0).Select(static a => a.Id).ToArray();

        Assert.Equal(ids[0], HitTester.HitTest(store, 0, new PointD(150, 150), 2)!.Annotation.Id); // inside a filled shape
        Assert.Null(HitTester.HitTest(store, 0, new PointD(350, 150), 2)); // inside an unfilled one
        Assert.Equal(ids[1], HitTester.HitTest(store, 0, new PointD(300, 150), 2)!.Annotation.Id); // on its stroke
        Assert.Null(HitTester.HitTest(store, 0, new PointD(150, 350), 2)); // the middle of an unfilled ellipse
        Assert.Equal(ids[2], HitTester.HitTest(store, 0, new PointD(200, 350), 2)!.Annotation.Id);
        Assert.Null(HitTester.HitTest(store, 0, new PointD(350, 330), 2)); // inside an unfilled polygon
        Assert.Equal(ids[3], HitTester.HitTest(store, 0, new PointD(350, 300), 2)!.Annotation.Id);
        Assert.NotNull(samples);
    }

    [Fact]
    public void AC04_AToleranceReachesAJustMissedHairlineAndATouchToleranceReachesFurther()
    {
        var (store, _) = StoreOf(s => s.Factory.Line(0, new PointD(100, 100), new PointD(300, 100), width: 0.25));
        var mouse = HitTester.ToPoints(HitTester.MouseToleranceDip, 1);
        var touch = HitTester.ToPoints(HitTester.TouchToleranceDip, 1);

        Assert.NotNull(HitTester.HitTest(store, 0, new PointD(200, 100 + 5), mouse)); // 5 away, 6 allowed
        Assert.Null(HitTester.HitTest(store, 0, new PointD(200, 100 + 9), mouse));
        Assert.NotNull(HitTester.HitTest(store, 0, new PointD(200, 100 + 9), touch));
        Assert.Equal(6, mouse);
        Assert.Equal(12, touch);
        Assert.Equal(3, HitTester.ToPoints(6, 2)); // at 200% zoom 6 DIP are 3 points
    }

    [Fact]
    public void AC04_TextMarkupHitsOnItsQuadsAndInkOnItsStrokeAndNotesAnywhereInTheirIcon()
    {
        var (store, _) = StoreOf(s => s.Highlight(), s => s.Ink(), s => s.Note());
        var highlight = store.OnPage(0)[0];
        var ink = store.OnPage(0)[1];
        var note = store.OnPage(0)[2];
        var quad = ((TextMarkupPayload)highlight.Payload).Quads[0];

        Assert.Equal(highlight.Id, HitTester.HitTest(store, 0, Geo.Center(quad.Bounds()), 1)!.Annotation.Id);
        Assert.Null(HitTester.HitTest(store, 0, new PointD(quad.Bounds().Right + 40, quad.Bounds().Top + 40), 1));
        var firstStroke = ((InkPayload)ink.Payload).Strokes[0];
        Assert.Equal(ink.Id, HitTester.HitTest(store, 0, new PointD(firstStroke.Xy[40], firstStroke.Xy[41]), 1)!.Annotation.Id);
        Assert.Equal(note.Id, HitTester.HitTest(store, 0, Geo.Center(note.Rect), 1)!.Annotation.Id);
    }

    [Fact]
    public void AC04_TenThousandAnnotationsHitTestInUnderAMillisecondEachThroughTheIndex()
    {
        var samples = new Samples();
        var store = new AnnotationStore();
        var random = new Random(4);
        for (var i = 0; i < 10_000; i++)
        {
            var x = random.Next(0, 580);
            var y = random.Next(0, 760);
            store.Add(samples.Factory.Box(MarkupKind.Rectangle, 0, new PdfRect(x, y, x + 20, y + 12), fill: Rgb.White) with { Id = "MikePDF:" + i.ToString("x32", System.Globalization.CultureInfo.InvariantCulture) });
        }

        _ = HitTester.HitTest(store, 0, new PointD(300, 300), 3); // builds the index
        var watch = Stopwatch.StartNew();
        var found = 0;
        for (var i = 0; i < 2_000; i++)
        {
            found += HitTester.HitTest(store, 0, new PointD(random.Next(0, 600), random.Next(0, 780)), 3) is null ? 0 : 1;
        }

        watch.Stop();
        Assert.True(watch.Elapsed.TotalMilliseconds / 2_000 < 1, $"{watch.Elapsed.TotalMilliseconds / 2_000:0.000} ms a hit test");
        Assert.True(found > 0);
    }

    [Fact]
    public void AC04_RepliesAreNeverHit()
    {
        var samples = new Samples();
        var store = new AnnotationStore();
        var note = samples.Note();
        store.Add(note);
        store.Add(samples.Factory.Reply(note, "yes"));

        Assert.Equal(note.Id, HitTester.HitTest(store, 0, Geo.Center(note.Rect), 1)!.Annotation.Id);
        Assert.Single(HitTester.HitAll(store, 0, Geo.Center(note.Rect), 1));
    }

    [Fact]
    public void Selection_BoxKindsHaveEightHandlesLinesTwoAndPolygonsBothAndTextMarkupNone()
    {
        var samples = new Samples();

        Assert.Equal(8, SelectionGeometry.Handles(samples.Rectangle()).Count);
        Assert.Equal(8, SelectionGeometry.Handles(samples.Stamp()).Count);
        Assert.Equal(8, SelectionGeometry.Handles(samples.FreeText()).Count);
        Assert.Empty(SelectionGeometry.Handles(samples.Typewriter())); // typewriter text only moves
        var line = SelectionGeometry.Handles(samples.Line());
        Assert.Equal([HandleKind.Vertex, HandleKind.Vertex], line.Select(static h => h.Kind));
        var polygon = SelectionGeometry.Handles(samples.Polygon());
        Assert.Equal(8 + 4, polygon.Count); // the box handles that scale it and a handle at each vertex
        Assert.Empty(SelectionGeometry.Handles(samples.Highlight()));
        Assert.Empty(SelectionGeometry.Handles(samples.Note()));
        Assert.Empty(SelectionGeometry.Handles(samples.Ink()));
        Assert.NotEmpty(SelectionGeometry.Outline(samples.Highlight())); // the outline of its quads instead
        var rect = samples.Rectangle();
        Assert.Equal(HandleKind.NorthWest, SelectionGeometry.HitHandle(rect, new PointD(rect.Rect.Left + 1, rect.Rect.Top - 1), 4)!.Value.Kind);
        Assert.Null(SelectionGeometry.HitHandle(rect, Geo.Center(rect.Rect), 4));
    }

    private static readonly PdfRect Page = new(0, 0, 612, 792);

    [Fact]
    public void AC05_AMoveKeepsAtLeastEightByEightPointsOnThePage()
    {
        var rect = new PdfRect(100, 100, 200, 160);

        var (dx, dy) = MoveResize.ClampMove(rect, -500, -500, Page);
        Assert.Equal((-192, -152), (Math.Round(dx, 6), Math.Round(dy, 6))); // right edge at 8, top edge at 8
        var (ex, ey) = MoveResize.ClampMove(rect, 5_000, 5_000, Page);
        Assert.Equal((612 - 8 - 100, 792 - 8 - 100), (Math.Round(ex, 6), Math.Round(ey, 6))); // left edge at 604, bottom edge at 784
        Assert.Equal((10.0, 20.0), MoveResize.ClampMove(rect, 10, 20, Page)); // a move that stays on the page is not touched

        // A rectangle smaller than 8 points keeps all of itself on the page.
        var (sx, _) = MoveResize.ClampMove(new PdfRect(100, 100, 105, 105), -500, 0, Page);
        Assert.Equal(-100 + 0, Math.Round(sx, 6));
    }

    [Theory]
    [InlineData(HandleKind.East, 30, 0, false, false, 100, 100, 230, 160)]
    [InlineData(HandleKind.North, 0, 25, false, false, 100, 100, 200, 185)]
    [InlineData(HandleKind.SouthWest, -10, -10, false, false, 90, 90, 200, 160)]
    [InlineData(HandleKind.East, 30, 0, false, true, 70, 100, 230, 160)] // Alt: from the centre
    [InlineData(HandleKind.SouthEast, 20, -20, true, false, 100, 80, 233.3333, 160)] // Shift: proportions kept (100:60), the larger change wins
    [InlineData(HandleKind.East, -500, 0, false, false, 100, 100, 104, 160)] // never below 4 points wide
    [InlineData(HandleKind.North, 0, -500, false, false, 100, 100, 200, 104)] // never below 4 points tall
    public void AC05_AResizeDragsTheHandleKeepsProportionsWithShiftWorksFromTheCentreWithAltAndHasAMinimum(HandleKind handle, double dx, double dy, bool shift, bool alt, double l, double b, double r, double t)
    {
        var result = MoveResize.Resize(new PdfRect(100, 100, 200, 160), handle, dx, dy, shift, alt, Page);

        Assert.Equal((l, b, r, t), (Math.Round(result.Left, 4), Math.Round(result.Bottom, 4), Math.Round(result.Right, 4), Math.Round(result.Top, 4)));
    }

    [Fact]
    public void AC05_AResizeStaysInsideThePageAndNudgesAreOneAndTenPoints()
    {
        var result = MoveResize.Resize(new PdfRect(500, 700, 600, 780), HandleKind.NorthEast, 500, 500, false, false, Page);

        Assert.Equal((612, 792), (result.Right, result.Top));
        Assert.Equal(1, MoveResize.NudgeDistance(shift: false));
        Assert.Equal(10, MoveResize.NudgeDistance(shift: true));
    }

    [Fact]
    public void SH05_VerticesAreInsertedAtTheMidpointRemovedWithinTheMinimumAndMoved()
    {
        var samples = new Samples();
        var triangle = samples.Factory.Polygon(0, [new PointD(0, 0), new PointD(100, 0), new PointD(50, 80)]);

        var inserted = VertexEdit.Insert(triangle, 0)!;
        Assert.Equal(new PointD(50, 0), ((PolyPayload)inserted.Payload).Vertices[1]);
        var wrapped = VertexEdit.Insert(triangle, 2)!; // a polygon closes: the segment from the last vertex to the first
        Assert.Equal(new PointD(25, 40), ((PolyPayload)wrapped.Payload).Vertices[3]);
        Assert.Null(VertexEdit.Remove(triangle, 1)); // refused below three
        Assert.Equal(3, ((PolyPayload)VertexEdit.Remove(inserted, 1)!.Payload).Vertices.Count);
        var moved = VertexEdit.Move(triangle, 2, new PointD(60, 120))!;
        Assert.Equal(120 + (moved.Width / 2), moved.Rect.Top, 6);

        var polyline = samples.Factory.PolyLine(0, [new PointD(0, 0), new PointD(10, 10)]);
        Assert.Null(VertexEdit.Remove(polyline, 0)); // a polyline keeps two
        Assert.Null(VertexEdit.Insert(polyline, 1)); // no segment after the last vertex of an open path
        Assert.NotNull(VertexEdit.Insert(polyline, 0));

        var scaled = VertexEdit.Scale(triangle, Geo.Inflate(new PdfRect(0, 0, 200, 160), triangle.Width / 2));
        Assert.Equal(new PointD(200, 0), ((PolyPayload)scaled.Payload).Vertices[1]);
    }
}
