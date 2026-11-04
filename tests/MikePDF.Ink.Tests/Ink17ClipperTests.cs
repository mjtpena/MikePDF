using MikePDF.Ink.Tests.Support;

namespace MikePDF.Ink.Tests;

/// <summary>INK-17: a stroke crossing the page edge is clipped; a stroke starting outside the box creates nothing.</summary>
public sealed class Ink17ClipperTests
{
    private static InkStroke Stroke(params (float X, float Y, float P)[] points) =>
        new("s", points.Select(p => new InkSample(p.X, p.Y, p.P)).ToArray());

    private static Func<string> Ids(string prefix = "n")
    {
        var n = 0;
        return () => $"{prefix}{++n}";
    }

    [Fact]
    public void Ink17_AStrokeInsideTheBoxIsReturnedUnchanged()
    {
        var stroke = Stroke((10, 10, 1), (50, 50, 0.5f), (100, 100, 1));
        var result = StrokeClipper.Clip(stroke, 0, 0, 100, 100, Ids());
        Assert.Same(stroke, Assert.Single(result));
    }

    [Fact]
    public void Ink17_AStrokeStartingOutsideTheBoxCreatesNothing()
    {
        Assert.Empty(StrokeClipper.Clip(Stroke((-1, 50, 1), (50, 50, 1), (60, 60, 1)), 0, 0, 100, 100, Ids()));
        Assert.Empty(StrokeClipper.Clip(Stroke((50, 101, 1)), 0, 0, 100, 100, Ids()));
    }

    [Fact]
    public void Ink17_AStrokeLeavingTheBoxIsCutAtTheEdgeWithInterpolatedPressure()
    {
        var stroke = Stroke((50, 50, 0.2f), (150, 50, 1f));
        var ids = Ids();
        var clipped = Assert.Single(StrokeClipper.Clip(stroke, 0, 0, 100, 100, ids));
        Assert.Equal("n1", clipped.Id);
        Assert.Equal(2, clipped.Samples.Count);
        Assert.Equal(new InkSample(50, 50, 0.2f), clipped.Samples[0]);
        Assert.Equal(100f, clipped.Samples[1].X, 4);
        Assert.Equal(50f, clipped.Samples[1].Y, 4);
        Assert.Equal(0.6f, clipped.Samples[1].Pressure, 4);
        Assert.Equal(2, stroke.Samples.Count); // the input is untouched
    }

    [Fact]
    public void Ink17_ReEnteringTheBoxDoesNotResumeTheStroke()
    {
        var stroke = Stroke((90, 50, 1), (110, 50, 1), (90, 60, 1), (80, 60, 1));
        var clipped = Assert.Single(StrokeClipper.Clip(stroke, 0, 0, 100, 100, Ids()));
        Assert.Equal(2, clipped.Samples.Count);
        Assert.Equal(100f, clipped.Samples[1].X, 4);
    }

    [Fact]
    public void Ink17_TheExitPointLiesOnTheEdgeForADiagonalExit()
    {
        var stroke = Stroke((10, 10, 1), (60, 90, 1), (120, 130, 1));
        var clipped = Assert.Single(StrokeClipper.Clip(stroke, 0, 0, 100, 100, Ids()));
        var exit = clipped.Samples[^1];
        Assert.True(Math.Abs(exit.X - 100) < 1e-3 || Math.Abs(exit.Y - 100) < 1e-3);
        Assert.InRange(exit.X, 0f, 100f);
        Assert.InRange(exit.Y, 0f, 100f);

        // The exit point is collinear with the segment it was cut from.
        var cross = ((exit.X - 60.0) * (130 - 90)) - ((exit.Y - 90.0) * (120 - 60));
        Assert.True(Math.Abs(cross) < 0.05, $"{cross}");
    }

    [Fact]
    public void Ink17_PointsOnTheBoxEdgeAreInside()
    {
        var onEdge = Stroke((0, 0, 1), (100, 0, 1), (100, 100, 1));
        Assert.Same(onEdge, Assert.Single(StrokeClipper.Clip(onEdge, 0, 0, 100, 100, Ids())));

        // A sample exactly on the edge followed by one outside does not repeat the point.
        var leaving = Stroke((50, 50, 1), (100, 50, 0.4f), (101, 50, 1));
        var clipped = Assert.Single(StrokeClipper.Clip(leaving, 0, 0, 100, 100, Ids()));
        Assert.Equal(2, clipped.Samples.Count);
    }

    [Fact]
    public void Ink17_AStrokeLeavingFromAnEdgeSampleKeepsThatSampleOnly()
    {
        var stroke = Stroke((100, 50, 0.3f), (150, 50, 1));
        var clipped = Assert.Single(StrokeClipper.Clip(stroke, 0, 0, 100, 100, Ids()));
        Assert.Equal(new InkSample(100, 50, 0.3f), Assert.Single(clipped.Samples));
    }

    [Fact]
    public void Ink17_NewIdIsOnlyCalledWhenAStrokeIsCut()
    {
        var calls = 0;
        string NewId() => $"c{++calls}";
        StrokeClipper.Clip(Stroke((1, 1, 1), (2, 2, 1)), 0, 0, 10, 10, NewId);
        StrokeClipper.Clip(Stroke((-1, 1, 1), (2, 2, 1)), 0, 0, 10, 10, NewId);
        Assert.Equal(0, calls);
        StrokeClipper.Clip(Stroke((1, 1, 1), (20, 2, 1)), 0, 0, 10, 10, NewId);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Ink17_ClippedTracesStayInsideTheBoxAndKeepTheirLeadingSamples()
    {
        var box = (L: 100.0, B: 100.0, R: 160.0, T: 160.0);
        var checkedAny = 0;
        foreach (var trace in Traces.Build(200))
        {
            var stroke = new InkStroke("t", trace.Samples);
            var result = StrokeClipper.Clip(stroke, box.L, box.B, box.R, box.T, Ids());
            var startsInside = trace.Samples[0].X >= box.L && trace.Samples[0].X <= box.R && trace.Samples[0].Y >= box.B && trace.Samples[0].Y <= box.T;
            if (!startsInside)
            {
                Assert.Empty(result);
                continue;
            }

            var clipped = Assert.Single(result);
            checkedAny++;
            foreach (var s in clipped.Samples)
            {
                Assert.InRange(s.X, (float)box.L, (float)box.R);
                Assert.InRange(s.Y, (float)box.B, (float)box.T);
            }

            if (!ReferenceEquals(clipped, stroke))
            {
                // Every kept sample except the interpolated exit point is an original leading sample.
                for (var i = 0; i < clipped.Samples.Count - 1; i++)
                {
                    Assert.Equal(trace.Samples[i], clipped.Samples[i]);
                }
            }
        }

        Assert.True(checkedAny > 20);
    }

    [Fact]
    public void Ink17_AnInvertedOrNonFiniteBoxIsRejected()
    {
        var stroke = Stroke((1, 1, 1));
        Assert.Throws<ArgumentException>(() => StrokeClipper.Clip(stroke, 10, 0, 0, 10, Ids()));
        Assert.Throws<ArgumentException>(() => StrokeClipper.Clip(stroke, 0, 10, 10, 0, Ids()));
        Assert.Throws<ArgumentException>(() => StrokeClipper.Clip(stroke, double.NaN, 0, 10, 10, Ids()));
        Assert.Throws<ArgumentException>(() => StrokeClipper.Clip(stroke, 0, 0, double.PositiveInfinity, 10, Ids()));
        Assert.Throws<ArgumentNullException>(() => StrokeClipper.Clip(null!, 0, 0, 1, 1, Ids()));
    }
}
