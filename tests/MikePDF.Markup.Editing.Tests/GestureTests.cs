#pragma warning disable CA1861 // test data: arrays written inline are the point of these cases

using MikePDF.Markup.Tests.Support;


namespace MikePDF.Markup.Editing.Tests;

/// <summary>SH-03: the arithmetic of the shape tools.</summary>
public sealed class GestureTests
{
    [Fact]
    public void ADragShorterThanFourPointsCreatesNothing()
    {
        Assert.Null(ShapeGestures.DragRect(new PointD(10, 10), new PointD(12, 12)));
        Assert.Null(ShapeGestures.DragLine(new PointD(10, 10), new PointD(13, 10)));
        Assert.NotNull(ShapeGestures.DragRect(new PointD(10, 10), new PointD(14, 10)));
        Assert.NotNull(ShapeGestures.DragLine(new PointD(10, 10), new PointD(10, 14)));
    }

    [Theory]
    [InlineData(false, false, 10, 20, 110, 70)]
    [InlineData(true, false, 10, 20, 110, 120)] // a square on the larger side
    [InlineData(false, true, -90, -30, 110, 70)] // the start is the centre
    [InlineData(true, true, -90, -80, 110, 120)]
    public void ARectangleDragHonoursShiftAndAlt(bool shift, bool alt, double l, double b, double r, double t)
    {
        var rect = ShapeGestures.DragRect(new PointD(10, 20), new PointD(110, 70), shift, alt)!.Value;

        Assert.Equal((l, b, r, t), (rect.Left, rect.Bottom, rect.Right, rect.Top));
    }

    [Fact]
    public void ADragToTheUpperLeftIsNormalisedAndShiftKeepsItsDirection()
    {
        var rect = ShapeGestures.DragRect(new PointD(100, 100), new PointD(40, 70), shift: true)!.Value;

        Assert.Equal((40, 40, 100, 100), (rect.Left, rect.Bottom, rect.Right, rect.Top));
        Assert.Equal(rect.Width, rect.Height);
    }

    [Theory]
    [InlineData(100, 5, 100.1249, 0)] // nearly flat: horizontal
    [InlineData(100, 100, 100, 100)] // already 45 degrees
    [InlineData(5, 100, 0, 100.1249)] // nearly vertical
    [InlineData(-100, 10, -100.4988, 0)]
    public void ALineDragWithShiftSnapsToMultiplesOfFortyFiveDegreesKeepingItsLength(double dx, double dy, double ex, double ey)
    {
        var (start, end) = ShapeGestures.DragLine(new PointD(0, 0), new PointD(dx, dy), shift: true)!.Value;

        Assert.Equal(0, start.X);
        Assert.Equal(ex, end.X, 3);
        Assert.Equal(ey, end.Y, 3);
        Assert.Equal(Math.Sqrt((dx * dx) + (dy * dy)), Geo.Distance(start, end), 6);
    }

    [Fact]
    public void ALineDragWithAltIsCentredOnTheStart()
    {
        var (start, end) = ShapeGestures.DragLine(new PointD(50, 50), new PointD(80, 90), alt: true)!.Value;

        Assert.Equal((20, 10), (start.X, start.Y));
        Assert.Equal((80, 90), (end.X, end.Y));
    }

    [Fact]
    public void ShiftOnATurnedPageIsAMultipleOfFortyFiveDegreesAsDisplayed()
    {
        // The drag is made in displayed coordinates and snapped there; converted to user space the line is still a multiple of 45 degrees, turned with the page.
        var frame = Samples.Frame(90);
        var (a, b) = ShapeGestures.DragLine(new PointD(100, 100), new PointD(190, 120), shift: true)!.Value;

        var user = (frame.ToUser(a), frame.ToUser(b));

        Assert.Equal(a.Y, b.Y, 9); // flat as displayed
        Assert.Equal(user.Item1.X, user.Item2.X, 9); // and vertical in user space, because the page is turned a quarter
    }

    [Fact]
    public void APolygonIsBuiltByClicksClosedByAClickNearTheFirstVertexAndCompletedWithThree()
    {
        var builder = new PolygonBuilder(closed: true);
        builder.Add(new PointD(0, 0));
        builder.Add(new PointD(100, 0));
        Assert.False(builder.CanComplete);
        Assert.Null(builder.Complete());
        Assert.Equal("Shape_NeedPolygon", builder.NeedKey);
        Assert.False(builder.ClosesAt(new PointD(2, 2), 8)); // two vertices cannot close
        builder.Add(new PointD(50, 80));

        Assert.True(builder.ClosesAt(new PointD(3, 4), 8));
        Assert.False(builder.ClosesAt(new PointD(30, 4), 8));
        Assert.Equal(3, builder.Complete()!.Count);
    }

    [Fact]
    public void BackspaceRemovesTheLastVertexAndAPolylineNeedsTwo()
    {
        var builder = new PolygonBuilder(closed: false);
        builder.Add(new PointD(0, 0));
        builder.Add(new PointD(10, 10));
        Assert.True(builder.CanComplete);
        Assert.Equal("Shape_NeedPolyline", builder.NeedKey);

        Assert.True(builder.Backspace());

        Assert.Single(builder.Vertices);
        Assert.False(builder.CanComplete);
        Assert.True(builder.Backspace());
        Assert.False(builder.Backspace());
        Assert.False(builder.ClosesAt(new PointD(0, 0), 100)); // a polyline never closes
    }

    [Fact]
    public void ADuplicateClickIsMergedOnCompletionAndTheVertexLimitIsAThousand()
    {
        var builder = new PolygonBuilder(closed: false);
        builder.Add(new PointD(0, 0));
        builder.Add(new PointD(0.001, 0));
        builder.Add(new PointD(10, 0));
        Assert.Equal(2, builder.Complete()!.Count);

        var full = new PolygonBuilder(closed: false);
        for (var i = 0; i < 1_000; i++)
        {
            Assert.True(full.Add(new PointD(i, i % 7)));
        }

        Assert.False(full.Add(new PointD(2_000, 0))); // further clicks are ignored
        Assert.Equal(1_000, full.Vertices.Count);
        Assert.NotNull(full.Complete());
    }
}
