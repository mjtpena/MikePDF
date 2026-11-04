using MikePDF.Ink.Tests.Support;

namespace MikePDF.Ink.Tests;

/// <summary>INK-06: eraser geometry. The "one command" and undo parts belong to the caller; here the gesture is one result and the inputs are never changed.</summary>
public sealed class Ink06EraserTests
{
    private static readonly InkStyle Pen = InkStyle.DefaultPen;
    private static readonly InkStyle Marker = InkStyle.DefaultHighlighter; // constant 14 pt, half width 7

    private static Func<string> Ids(string prefix = "p")
    {
        var n = 0;
        return () => $"{prefix}{++n}";
    }

    // A horizontal stroke along y with a sample every `step` points and pressure rising 0 to 1.
    private static InkStroke Horizontal(string id, float y, float x0 = 0, float x1 = 100, float step = 1f)
    {
        var samples = new List<InkSample>();
        for (var x = x0; x <= x1 + 1e-4f; x += step)
        {
            samples.Add(new InkSample(x, y, (x - x0) / (x1 - x0)));
        }

        return new InkStroke(id, samples);
    }

    private static InkSample[] Snapshot(InkStroke s) => [.. s.Samples];

    // ---- Stroke mode -------------------------------------------------------------------------------

    [Fact]
    public void Ink06_StrokeModeRemovesExactlyTheTouchedStrokes()
    {
        var strokes = new[] { Horizontal("a", 0), Horizontal("b", 10), Horizontal("c", 20), Horizontal("d", 30) };
        var ids = StrokeEraser.TouchedIds(strokes, Marker, 50, 15, 3.5); // marker half width 7: b (dist 5) and c (dist 5) touched, a and d not (15 > 10.5)
        Assert.Equal(["b", "c"], ids);

        var result = StrokeEraser.EraseGesture(strokes, Marker, EraserMode.Stroke, [(50, 15)], 3.5, Ids());
        Assert.Equal(["b", "c"], result.RemovedIds);
        Assert.Empty(result.Added);
    }

    [Fact]
    public void Ink06_ATouchNeedsTheCircleToReachTheOutlineNotTheCentreline()
    {
        var stroke = Horizontal("a", 0);
        Assert.True(StrokeEraser.Touches(stroke, Marker, 50, 10, 3.01)); // 10 <= 3.01 + 7
        Assert.False(StrokeEraser.Touches(stroke, Marker, 50, 10, 2.9));
        Assert.True(StrokeEraser.Touches(stroke, Marker, 50, 0, 0)); // a zero radius on the centreline
        Assert.False(StrokeEraser.Touches(stroke, Marker, 150, 0, 3)); // beyond the end
        Assert.True(StrokeEraser.Touches(stroke, Marker, 106, 0, 0)); // within the round cap
    }

    [Fact]
    public void Ink06_TheWidthAtTheClosestPointDecidesAVariableWidthTouch()
    {
        // Pressure rises along the stroke, so it is thin at the left and thick at the right.
        var stroke = Horizontal("a", 0);
        var thinSide = StrokeEraser.Touches(stroke, Pen, 5, 1.1, 0.2);
        var thickSide = StrokeEraser.Touches(stroke, Pen, 95, 1.1, 0.2);
        Assert.False(thinSide);
        Assert.True(thickSide);
    }

    [Fact]
    public void Ink06_AOneSampleStrokeIsADisc()
    {
        var dot = new InkStroke("dot", [new InkSample(10, 10, 1f)]);
        Assert.True(StrokeEraser.Touches(dot, Marker, 10, 20, 3.5)); // 10 <= 3.5 + 7
        Assert.False(StrokeEraser.Touches(dot, Marker, 10, 21, 3.5));
        Assert.True(StrokeEraser.Touches(dot, Pen, 10, 12.5, 1.6)); // base width 2, half 1: 2.5 <= 1.6 + 1
    }

    // ---- Point mode --------------------------------------------------------------------------------

    [Fact]
    public void Ink06_PointModeSplitsAStrokeIntoTwoPiecesWithCutPointsOnTheCircle()
    {
        var stroke = Horizontal("a", 5, 0, 100, 1f);
        var before = Snapshot(stroke);
        var pieces = StrokeEraser.SplitPoint(stroke, Pen, 50, 5, 4.5, Ids());
        Assert.Equal(2, pieces.Count);
        Assert.Equal(["p1", "p2"], pieces.Select(p => p.Id));

        var left = pieces[0].Samples;
        var right = pieces[1].Samples;
        Assert.Equal(before[0], left[0]);
        Assert.Equal(45.5f, left[^1].X, 4); // samples at 46..54 are inside r=4.5, so the cut is on the circle at 45.5
        Assert.Equal(5f, left[^1].Y, 4);
        Assert.Equal(0.455f, left[^1].Pressure, 3);
        Assert.Equal(54.5f, right[0].X, 4);
        Assert.Equal(before[^1], right[^1]);
        Assert.Equal(before[^1].Pressure, right[^1].Pressure);

        // The pieces keep the pressure of the samples they kept.
        for (var i = 0; i < left.Count - 1; i++)
        {
            Assert.Equal(before[i], left[i]);
        }

        for (var i = 1; i < right.Count; i++)
        {
            Assert.Equal(before[55 + i - 1], right[i]);
        }

        // Nothing of either piece lies inside the circle, and the original is untouched.
        Assert.All(pieces.SelectMany(p => p.Samples), s => Assert.True(Math.Sqrt(Geometry.Sq(s.X - 50) + Geometry.Sq(s.Y - 5)) >= 4.5 - 1e-4));
        Assert.Equal(before, Snapshot(stroke));
    }

    [Fact]
    public void Ink06_AChordThroughTheCircleBetweenTwoFarSamplesIsCut()
    {
        var stroke = new InkStroke("a", [new InkSample(0, 0, 0f), new InkSample(100, 0, 1f)]);
        var pieces = StrokeEraser.SplitPoint(stroke, Pen, 50, 2, 5, Ids());
        Assert.Equal(2, pieces.Count);
        var halfChord = Math.Sqrt(25 - 4);
        Assert.Equal(50 - halfChord, pieces[0].Samples[^1].X, 3);
        Assert.Equal(50 + halfChord, pieces[1].Samples[0].X, 3);
        Assert.Equal((50 - halfChord) / 100, pieces[0].Samples[^1].Pressure, 3);
        Assert.Equal(2, pieces[0].Samples.Count);
        Assert.Equal(2, pieces[1].Samples.Count);
    }

    [Fact]
    public void Ink06_ErasingTheEndOfAStrokeLeavesOnePiece()
    {
        var stroke = Horizontal("a", 0);
        var pieces = StrokeEraser.SplitPoint(stroke, Pen, 0, 0, 3.5, Ids());
        var piece = Assert.Single(pieces);
        Assert.Equal(3.5f, piece.Samples[0].X, 4);
        Assert.Equal(100f, piece.Samples[^1].X);

        var tail = StrokeEraser.SplitPoint(stroke, Pen, 100, 0, 3.5, Ids());
        Assert.Equal(96.5f, Assert.Single(tail).Samples[^1].X, 4);
    }

    [Fact]
    public void Ink06_NothingInsideReturnsTheOriginalInstance()
    {
        var stroke = Horizontal("a", 0);
        var result = StrokeEraser.SplitPoint(stroke, Pen, 50, 20, 5, Ids());
        Assert.Same(stroke, Assert.Single(result));
        Assert.Same(stroke, Assert.Single(StrokeEraser.SplitPoint(stroke, Pen, 50, 5, 5, Ids()))); // tangent from outside removes nothing
        Assert.Same(stroke, Assert.Single(StrokeEraser.SplitPoint(stroke, Pen, 50, 0, 0, Ids())));
    }

    [Fact]
    public void Ink06_EverythingInsideGivesNoPiecesAndASingleSampleOutsideIsKept()
    {
        var stroke = Horizontal("a", 0, 0, 6);
        Assert.Empty(StrokeEraser.SplitPoint(stroke, Pen, 3, 0, 20, Ids()));

        var dot = new InkStroke("dot", [new InkSample(0, 0, 1)]);
        Assert.Empty(StrokeEraser.SplitPoint(dot, Pen, 0, 0, 1, Ids()));
        Assert.Same(dot, Assert.Single(StrokeEraser.SplitPoint(dot, Pen, 9, 0, 1, Ids())));
    }

    [Fact]
    public void Ink06_AGestureAppliesEachCircleToTheResultOfThePreviousOne()
    {
        var stroke = Horizontal("a", 0);
        var ids = Ids();
        var path = new List<(double X, double Y)> { (20, 0), (50, 0), (80, 0) };
        var result = StrokeEraser.EraseGesture([stroke], Pen, EraserMode.Point, path, 4.5, ids);
        Assert.Equal(["a"], result.RemovedIds);
        Assert.Equal(4, result.Added.Count);
        Assert.Equal(result.Added.Count, result.Added.Select(p => p.Id).Distinct().Count());

        // Pieces are in stroke order and none overlaps an erased circle.
        var starts = result.Added.Select(p => p.Samples[0].X).ToArray();
        Assert.Equal(starts.OrderBy(x => x), starts);
        Assert.All(result.Added.SelectMany(p => p.Samples), s =>
            Assert.All(path, c => Assert.True(Math.Sqrt(Geometry.Sq(s.X - c.X) + Geometry.Sq(s.Y - c.Y)) >= 4.5 - 1e-4)));
    }

    [Fact]
    public void Ink06_AGestureThatErasesEverythingRemovesTheStrokeAndAddsNothing()
    {
        var stroke = Horizontal("a", 0);
        var path = Enumerable.Range(0, 21).Select(i => ((double)i * 5, 0.0)).ToList();
        var result = StrokeEraser.EraseGesture([stroke], Pen, EraserMode.Point, path, 4, Ids());
        Assert.Equal(["a"], result.RemovedIds);
        Assert.Empty(result.Added);
    }

    [Fact]
    public void Ink06_PointModeLeavesUntouchedStrokesAlone()
    {
        var a = Horizontal("a", 0);
        var b = Horizontal("b", 50);
        var result = StrokeEraser.EraseGesture([a, b], Pen, EraserMode.Point, [(50, 0)], 3, Ids());
        Assert.Equal(["a"], result.RemovedIds);
        Assert.Equal(2, result.Added.Count);
        Assert.DoesNotContain(result.Added, p => p.Id == "b");
    }

    [Fact]
    public void Ink06_InputStrokesAreNeverChangedSoUndoCanRestoreThemExactly()
    {
        var strokes = new[] { Horizontal("a", 0), Horizontal("b", 10) };
        var before = strokes.Select(Snapshot).ToArray();
        StrokeEraser.EraseGesture(strokes, Pen, EraserMode.Point, [(30, 0), (60, 5), (70, 10)], 6, Ids());
        StrokeEraser.EraseGesture(strokes, Pen, EraserMode.Stroke, [(30, 0), (60, 5)], 6, Ids());
        for (var i = 0; i < strokes.Length; i++)
        {
            Assert.Equal(before[i], Snapshot(strokes[i]));
        }
    }

    [Fact]
    public void Ink06_AGestureIsDeterministicForTheSameIds()
    {
        var strokes = Traces.Build(10).Select(t => new InkStroke($"t{t.Name}", t.Samples)).ToArray();
        var path = Enumerable.Range(0, 30).Select(i => (100.0 + (i * 2), 100.0 + (i * 1.5))).ToList();
        var first = StrokeEraser.EraseGesture(strokes, Pen, EraserMode.Point, path, 6, Ids());
        var second = StrokeEraser.EraseGesture(strokes, Pen, EraserMode.Point, path, 6, Ids());
        Assert.Equal(first.RemovedIds, second.RemovedIds);
        Assert.Equal(first.Added.Select(s => s.Id), second.Added.Select(s => s.Id));
        Assert.Equal(first.Added.SelectMany(s => s.Samples), second.Added.SelectMany(s => s.Samples));
    }

    [Fact]
    public void Ink06_PointErasingTracesNeverLeavesASampleInsideTheCircle()
    {
        foreach (var trace in Traces.Build(100))
        {
            var stroke = new InkStroke("t", trace.Samples);
            var mid = trace.Samples[trace.Samples.Length / 2];
            var pieces = StrokeEraser.SplitPoint(stroke, Pen, mid.X, mid.Y, 3, Ids());
            Assert.False(pieces.Count == 1 && ReferenceEquals(pieces[0], stroke), trace.Name);
            foreach (var s in pieces.SelectMany(p => p.Samples))
            {
                Assert.True(Math.Sqrt(Geometry.Sq(s.X - mid.X) + Geometry.Sq(s.Y - mid.Y)) >= 3 - 1e-3, trace.Name);
            }
        }
    }

    // ---- Arguments ---------------------------------------------------------------------------------

    private sealed class HugeList : IReadOnlyList<(double X, double Y)>
    {
        public int Count => StrokeLimits.MaxGesturePoints + 1;

        public (double X, double Y) this[int index] => (0, 0);

        public IEnumerator<(double X, double Y)> GetEnumerator() => throw new NotSupportedException();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => throw new NotSupportedException();
    }

    [Fact]
    public void Ink06_HostileArgumentsAreRejected()
    {
        var stroke = Horizontal("a", 0);
        Assert.Throws<ArgumentException>(() => StrokeEraser.Touches(stroke, Pen, double.NaN, 0, 1));
        Assert.Throws<ArgumentException>(() => StrokeEraser.Touches(stroke, Pen, 0, 0, -1));
        Assert.Throws<ArgumentException>(() => StrokeEraser.Touches(stroke, Pen, 0, 0, double.PositiveInfinity));
        Assert.Throws<ArgumentException>(() => StrokeEraser.SplitPoint(stroke, Pen, 0, double.NaN, 1, Ids()));
        Assert.Throws<ArgumentException>(() => StrokeEraser.EraseGesture([stroke], Pen, EraserMode.Point, [(0, double.NaN)], 1, Ids()));
        Assert.Throws<ArgumentException>(() => StrokeEraser.EraseGesture([stroke], Pen, EraserMode.Point, new HugeList(), 1, Ids()));
        Assert.Throws<ArgumentNullException>(() => StrokeEraser.TouchedIds(null!, Pen, 0, 0, 1));
        var empty = StrokeEraser.EraseGesture([], Pen, EraserMode.Stroke, [(0, 0)], 1, Ids());
        Assert.Empty(empty.RemovedIds);
        Assert.Empty(empty.Added);
        var noPath = StrokeEraser.EraseGesture([stroke], Pen, EraserMode.Point, [], 1, Ids());
        Assert.Empty(noPath.RemovedIds);
    }
}
