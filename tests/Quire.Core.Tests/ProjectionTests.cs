using Quire.Core.Geometry;
using Quire.Core.Layout;

namespace Quire.Core.Tests;

public sealed class ProjectionTests
{
    private static ViewportController Controller(int pages = 5, int turns = 0, double zoom = 1.0)
    {
        var controller = new ViewportController();
        controller.SetViewport(800, 600, 1.0);
        controller.SetPageSizes([.. Enumerable.Repeat(new SizeD(612, 792), pages)]);
        controller.Restore(0, 0, zoom, FitMode.None, turns);
        return controller;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void APointOfAPageGoesToTheViewportAndComesBack(int turns)
    {
        var controller = Controller(turns: turns);
        controller.GoToPage(2);
        var view = controller.Snapshot();
        var projection = PageProjection.For(view, 2)!;
        Assert.Equal(new SizeD(612, 792), projection.PageSize);
        foreach (var point in new[] { new PointD(0, 0), new PointD(100, 200), new PointD(612, 792), new PointD(300.5, 10.25) })
        {
            var onScreen = projection.PageToViewport(point);
            var back = projection.ViewportToPage(onScreen);
            Assert.Equal(point.X, back.X, 6);
            Assert.Equal(point.Y, back.Y, 6);
        }

        // The page's corners are the corners of its rectangle on screen, whatever the rotation.
        var box = projection.PageToViewport(new RectD(0, 0, 612, 792));
        Assert.Equal(projection.PageRect.X, box.X, 3);
        Assert.Equal(projection.PageRect.Width, box.Width, 3);
        Assert.Equal(projection.PageRect.Height, box.Height, 3);
    }

    [Fact]
    public void TheTopLeftOfThePageIsWhereTheViewRotationPutsIt()
    {
        var upright = PageProjection.For(Controller(turns: 0).Snapshot(), 0)!;
        var turned = PageProjection.For(Controller(turns: 1).Snapshot(), 0)!;
        // Turned clockwise a quarter, the page's top left corner is at the top right of the turned page.
        Assert.Equal(upright.PageToViewport(new PointD(0, 0)).X, upright.PageRect.X, 3);
        Assert.Equal(turned.PageRect.Right, turned.PageToViewport(new PointD(0, 0)).X, 3);
        Assert.Equal(turned.PageRect.Y, turned.PageToViewport(new PointD(0, 0)).Y, 3);
    }

    [Fact]
    public void TheProjectionFollowsTheScrollPosition()
    {
        var controller = Controller();
        var before = PageProjection.For(controller.Snapshot(), 0)!;
        controller.ScrollBy(0, 100);
        var after = PageProjection.For(controller.Snapshot(), 0)!;
        Assert.Equal(before.PageRect.Y - 100, after.PageRect.Y, 3);
        Assert.Equal(before.PageRect.X, after.PageRect.X, 3);
    }

    [Fact]
    public void AnyPointOfTheViewportBelongsToANearestPage()
    {
        var controller = Controller();
        var view = controller.Snapshot();
        Assert.Equal(0, PageProjection.At(view, new PointD(10, 10))!.Page);
        controller.GoToPage(3);
        var third = controller.Snapshot();
        Assert.Equal(3, PageProjection.At(third, new PointD(400, 300))!.Page);
        Assert.Null(PageProjection.For(view, -1));
        Assert.Null(PageProjection.For(view, 99));
        Assert.Null(PageProjection.At(new ViewportController().Snapshot(), new PointD(1, 1)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void GoingToAPointPutsItNearTheTopOfTheViewport(int turns)
    {
        var controller = Controller(turns: turns, zoom: 1.0);
        var target = new PointD(300, 500);
        controller.GoToPoint(3, target, useX: false);
        var projection = PageProjection.For(controller.Snapshot(), 3)!;
        var onScreen = projection.PageToViewport(target);
        // The point is within the margin of the top edge (or, when the page is turned, wherever the turn puts "top"), and on screen.
        Assert.InRange(onScreen.Y, -1, controller.ViewportHeight);
        Assert.True(onScreen.Y <= ViewportController.MarginDip + 1);
    }

    [Fact]
    public void GoingToAPointOnAWidePageAlsoBringsItToTheLeftEdgeWhenAskedTo()
    {
        var controller = Controller(zoom: 4.0);
        controller.GoToPoint(1, new PointD(200, 100), useX: true);
        var projection = PageProjection.For(controller.Snapshot(), 1)!;
        var onScreen = projection.PageToViewport(new PointD(200, 100));
        Assert.InRange(onScreen.X, -1, 20);
        Assert.InRange(onScreen.Y, -1, 20);

        var keepsX = Controller(zoom: 4.0);
        keepsX.ScrollBy(300, 0);
        var x = keepsX.ScrollX;
        keepsX.GoToPoint(1, new PointD(200, 100), useX: false);
        Assert.Equal(x, keepsX.ScrollX);
    }

    [Fact]
    public void GoingToAPointOutsideThePageStaysOnThePageAndAnEmptyDocumentIsLeftAlone()
    {
        var controller = Controller();
        controller.GoToPoint(2, new PointD(-9999, 99999), useX: true);
        Assert.InRange(controller.ScrollY, controller.Layout.Top(2), controller.Layout.Top(3)); // clamped to the page, the bottom of it at most
        controller.GoToPoint(999, new PointD(0, 0), useX: false);
        Assert.Equal(4, controller.CurrentPage);

        var empty = new ViewportController();
        empty.GoToPoint(0, new PointD(1, 1), useX: true);
        Assert.Equal(0, empty.ScrollY);
    }

    [Fact]
    public void ARectOfOnePageCanBeUnionedMeasuredAndTested()
    {
        var a = new RectD(0, 0, 10, 10);
        var b = new RectD(20, 5, 10, 10);
        Assert.Equal(new RectD(0, 0, 30, 15), a.Union(b));
        Assert.True(a.Contains(new PointD(5, 5)));
        Assert.False(a.Contains(new PointD(10, 5)));
        Assert.Equal(0, a.DistanceTo(new PointD(5, 5)));
        Assert.Equal(5, a.DistanceTo(new PointD(15, 5)));
        Assert.Equal(5, a.DistanceTo(new PointD(5, -5)));
        Assert.Equal(Math.Sqrt(50), a.DistanceTo(new PointD(15, 15)), 6);
    }
}
