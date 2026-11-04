using MikePDF.Core.Geometry;
using MikePDF.Core.Layout;

namespace MikePDF.Core.Tests;

/// <summary>
/// Who may move the view with a manipulation (a drag, a pinch, inertia), and what UI Automation's ScrollIntoView aligns. WinUI raises manipulations for a mouse or a pen drag as well as
/// for touch, so a drag that selects text must not also pan the document or turn the page.
/// </summary>
public sealed class ManipulationPolicyTests
{
    [Theory]
    [InlineData(PointerKind.Touch, false, true)]
    [InlineData(PointerKind.Touch, true, true)]
    [InlineData(PointerKind.Pen, false, true)]
    [InlineData(PointerKind.Pen, true, false)]
    [InlineData(PointerKind.Mouse, false, false)]
    [InlineData(PointerKind.Mouse, true, false)]
    [InlineData(PointerKind.Touchpad, false, false)]
    [InlineData(PointerKind.Touchpad, true, false)]
    public void OnlyAFingerAndAPenThatIsNotSelectingMayMoveTheView(PointerKind device, bool selecting, bool expected) =>
        Assert.Equal(expected, ManipulationPolicy.MayMoveView(device, selecting));

    [Fact]
    public void AnUnknownDeviceMovesNothing() => Assert.False(ManipulationPolicy.MayMoveView((PointerKind)99, false));

    // ---- ScrollIntoView ------------------------------------------------------------------------------------------------------------

    private static ViewportController Controller(int pages = 6)
    {
        var controller = new ViewportController();
        controller.SetViewport(1000, 600, 1.0);
        controller.SetPageSizes([.. Enumerable.Repeat(new SizeD(612, 792), pages)]);
        controller.Restore(0, 0, 1.0, FitMode.None, 0);
        return controller;
    }

    private static RectD Line(double y) => new(72, y, 300, 12);

    private static RectD OnScreen(ViewportController c, int page, RectD rect) => PageProjection.For(c.Snapshot(), page)!.PageToViewport(rect);

    [Fact]
    public void ARangeThatIsAllInViewIsNotScrolledWhicheverEndIsAskedFor()
    {
        foreach (var alignToTop in new[] { true, false })
        {
            var c = Controller();
            c.GoToPage(2);
            var before = c.ScrollY;
            var first = Line(100);
            var last = Line(250);
            Assert.False(c.NeedsRevealRange(2, first, 2, last));
            c.RevealPageRange(2, first, 2, last, alignToTop);
            Assert.Equal(before, c.ScrollY);
        }
    }

    [Fact]
    public void AlignToTopPutsTheStartOfTheRangeAtTheTopOfTheWindow()
    {
        var c = Controller();
        c.GoToPage(2);
        var first = Line(100);
        var last = Line(520); // below the window: the page is taller than it
        Assert.True(OnScreen(c, 2, last).Bottom > c.ViewportHeight);

        c.RevealPageRange(2, first, 2, last, alignToTop: true);

        Assert.Equal(0, OnScreen(c, 2, first).Y, 3);
    }

    [Fact]
    public void AlignToBottomPutsTheEndOfTheRangeAtTheBottomOfTheWindow()
    {
        var c = Controller();
        c.GoToPage(2);
        var first = Line(100);
        var last = Line(520);

        c.RevealPageRange(2, first, 2, last, alignToTop: false);

        Assert.Equal(c.ViewportHeight, OnScreen(c, 2, last).Bottom, 3);
    }

    [Fact]
    public void ScrollingTheEndOfASelectionIntoViewDoesNotJumpBackToWhereItBegan()
    {
        // The selection began on page 2 and has been dragged down to page 4; the window is on page 4. A client that scrolls the end into view must keep it there.
        var c = Controller();
        c.GoToPage(4);
        var first = Line(100);
        var last = Line(300);
        Assert.True(OnScreen(c, 2, first).Bottom < 0);

        c.RevealPageRange(2, first, 4, last, alignToTop: false);

        Assert.Equal(c.ViewportHeight, OnScreen(c, 4, last).Bottom, 3);
        Assert.True(OnScreen(c, 2, first).Bottom < 0, "the start is still above the window");
    }

    [Fact]
    public void ARevealWithoutAnAlignmentStillCentresABoxThatIsOffTheWindow()
    {
        var c = Controller();
        c.GoToPage(2);
        var rect = Line(600);
        Assert.True(OnScreen(c, 2, rect).Bottom > c.ViewportHeight);

        c.RevealPageRect(2, rect);

        var box = OnScreen(c, 2, rect);
        Assert.Equal(c.ViewportHeight / 2, box.Y + (box.Height / 2), 3);
    }
}
