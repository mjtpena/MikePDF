using Quire.Core.Geometry;
using Quire.Core.Text;
using static Quire.Core.Tests.TextFixtures;

namespace Quire.Core.Tests;

internal sealed class LoadedPages(params TextModel?[] pages) : ITextModelSource
{
    public int PageCount => pages.Length;

    public TextModel? GetLoaded(int page) => page >= 0 && page < pages.Length ? pages[page] : null;
}

public sealed class SelectionTests
{
    private static TextModel Page(int number, params Run[] runs) => TextModelBuilder.Build(Chars(runs), Letter, number);

    private static LoadedPages Two() => new(
        Page(0, new Run("alpha beta", 72, 72), new Run("gamma delta", 72, 88)),
        Page(1, new Run("epsilon zeta", 72, 72)));

    [Fact]
    public void PositionsAndSelectionsAreOrderedWhicheverEndComesFirst()
    {
        var a = new TextPosition(1, 3);
        var b = new TextPosition(0, 90);
        Assert.True(b < a);
        Assert.True(a > b);
        Assert.True(a >= new TextPosition(1, 3));
        Assert.True(b <= a);
        var backwards = new TextSelection(a, b);
        Assert.Equal(b, backwards.Start);
        Assert.Equal(a, backwards.End);
        Assert.False(backwards.IsEmpty);
        Assert.True(new TextSelection(a, a).IsEmpty);
        Assert.Equal((5, 8), new TextSelection(new TextPosition(0, 5), new TextPosition(0, 8)).RangeOnPage(0, 20));
        Assert.Equal((0, 0), backwards.RangeOnPage(7, 20));
        // Across pages: the tail of the first, all of a middle one, the head of the last.
        var across = new TextSelection(new TextPosition(0, 5), new TextPosition(2, 4));
        Assert.Equal((5, 20), across.RangeOnPage(0, 20));
        Assert.Equal((0, 9), across.RangeOnPage(1, 9));
        Assert.Equal((0, 4), across.RangeOnPage(2, 20));
        Assert.Equal((0, 20), new TextSelection(TextPosition.StartOf(0), TextPosition.EndOf(0)).RangeOnPage(0, 20));
    }

    [Fact]
    public void ADragSelectsFromWhereItBeganAndClearingEmptiesIt()
    {
        var selection = new SelectionController(Two());
        var changes = 0;
        selection.Changed += () => changes++;
        selection.Begin(new TextPosition(0, 2));
        Assert.False(selection.HasSelection);
        selection.DragTo(new TextPosition(0, 7));
        Assert.True(selection.HasSelection);
        selection.DragTo(new TextPosition(0, 7)); // no change, no event
        selection.Clear();
        Assert.False(selection.HasSelection);
        Assert.Equal(3, changes);
        selection.Select(new TextPosition(0, 1), new TextPosition(1, 3));
        Assert.True(selection.HasSelection);
    }

    [Fact]
    public void DoubleAndTripleClicksSelectAWordAndALine()
    {
        var selection = new SelectionController(Two());
        Assert.True(selection.SelectUnitAt(new TextPosition(0, 7), TextUnitKind.Word)); // inside "beta"
        Assert.Equal(new TextSelection(new TextPosition(0, 6), new TextPosition(0, 10)), selection.Selection); // "beta", not the space after it
        Assert.True(selection.SelectUnitAt(new TextPosition(0, 7), TextUnitKind.Line));
        Assert.Equal(0, selection.Selection.Start.Atom);
        Assert.False(selection.SelectUnitAt(new TextPosition(5, 0), TextUnitKind.Word)); // a page that is not loaded
        Assert.True(selection.SelectUnitAt(new TextPosition(0, 9999), TextUnitKind.Word)); // past the end: the last word
    }

    [Fact]
    public void ADoubleClickedWordLeavesOutThePunctuationThatClingsToIt()
    {
        var selection = new SelectionController(new LoadedPages(Page(0, new Run("see \"margin,\" -- ok", 72, 72))));
        Assert.True(selection.SelectUnitAt(new TextPosition(0, 7), TextUnitKind.Word)); // inside "margin"
        Assert.Equal(new TextSelection(new TextPosition(0, 5), new TextPosition(0, 11)), selection.Selection); // margin, without the quotes and the comma
        Assert.True(selection.SelectUnitAt(new TextPosition(0, 15), TextUnitKind.Word)); // the dashes alone are the word: nothing is trimmed away
        Assert.Equal(new TextSelection(new TextPosition(0, 14), new TextPosition(0, 16)), selection.Selection);
    }

    [Fact]
    public void SelectAllCoversEveryPageWithoutAnyOfThemLoaded()
    {
        var selection = new SelectionController(new LoadedPages(null, null, null));
        selection.SelectAll();
        Assert.Equal(new TextSelection(TextPosition.StartOf(0), TextPosition.EndOf(2)), selection.Selection);
        var none = new SelectionController(new LoadedPages());
        none.SelectAll();
        Assert.False(none.HasSelection);
    }

    [Fact]
    public void ShiftArrowsGrowTheSelectionByCharacterWordAndLine()
    {
        var selection = new SelectionController(Two());
        var start = new TextPosition(0, 0);
        Assert.Equal(ExtendOutcome.Moved, selection.Extend(ExtendKind.Character, +1, start));
        Assert.Equal(1, selection.Selection.Active.Atom);
        Assert.Equal(ExtendOutcome.Moved, selection.Extend(ExtendKind.Character, +1, start));
        Assert.Equal(2, selection.Selection.Active.Atom);
        Assert.Equal(ExtendOutcome.Moved, selection.Extend(ExtendKind.Character, -1, start));
        Assert.Equal(1, selection.Selection.Active.Atom);

        selection.Clear();
        Assert.Equal(ExtendOutcome.Moved, selection.Extend(ExtendKind.Word, +1, start));
        Assert.Equal(5, selection.Selection.Active.Atom); // the end of "alpha", not the start of "beta"
        Assert.Equal(ExtendOutcome.Moved, selection.Extend(ExtendKind.Word, +1, start));
        Assert.Equal(10, selection.Selection.Active.Atom);
        Assert.Equal(ExtendOutcome.Moved, selection.Extend(ExtendKind.Word, -1, start));
        Assert.Equal(6, selection.Selection.Active.Atom);

        selection.Clear();
        selection.Select(new TextPosition(0, 2), new TextPosition(0, 2));
        Assert.Equal(ExtendOutcome.Moved, selection.Extend(ExtendKind.Line, +1, start));
        var below = selection.Selection.Active.Atom;
        Assert.InRange(below, 11, 14); // the same column on the next line ("gamma delta" starts at atom 11)
        Assert.Equal(ExtendOutcome.Moved, selection.Extend(ExtendKind.Line, -1, start));
        Assert.Equal(2, selection.Selection.Active.Atom);
        Assert.Equal(ExtendOutcome.Moved, selection.Extend(ExtendKind.Line, +1, start));
        Assert.Equal(below, selection.Selection.Active.Atom);
    }

    [Fact]
    public void TheKeyboardCarriesTheSelectionOverToTheNextPageWhenItsTextIsThere()
    {
        var selection = new SelectionController(Two());
        var start = new TextPosition(0, 0);
        selection.Select(new TextPosition(0, 0), new TextPosition(0, Two().GetLoaded(0)!.Length));
        Assert.Equal(ExtendOutcome.Moved, selection.Extend(ExtendKind.Character, +1, start));
        Assert.Equal(new TextPosition(1, 0), selection.Selection.Active);
        Assert.Equal(ExtendOutcome.Moved, selection.Extend(ExtendKind.Character, -1, start));
        Assert.Equal(0, selection.Selection.Active.Page);

        selection.Select(new TextPosition(0, 0), new TextPosition(0, 0));
        Assert.Equal(ExtendOutcome.AtEdge, selection.Extend(ExtendKind.Character, -1, start)); // the start of the document
        selection.Select(new TextPosition(0, 0), new TextPosition(1, Two().GetLoaded(1)!.Length));
        Assert.Equal(ExtendOutcome.AtEdge, selection.Extend(ExtendKind.Character, +1, start));  // and the end
        Assert.Equal(ExtendOutcome.AtEdge, selection.Extend(ExtendKind.Character, 0, start));
    }

    [Fact]
    public void ARequestForAPageThatIsNotLoadedSaysWhichOne()
    {
        var pages = new LoadedPages(Page(0, new Run("abc", 72, 72)), null);
        var selection = new SelectionController(pages);
        selection.Select(new TextPosition(0, 0), new TextPosition(0, 3));
        Assert.Equal(ExtendOutcome.NeedsPage, selection.Extend(ExtendKind.Character, +1, default));
        Assert.Equal(1, selection.PageNeeded);
        Assert.Equal(ExtendOutcome.NeedsPage, selection.Extend(ExtendKind.Line, +1, default));

        var elsewhere = new SelectionController(pages);
        elsewhere.Select(new TextPosition(1, 0), new TextPosition(1, 0));
        Assert.Equal(ExtendOutcome.NeedsPage, elsewhere.Extend(ExtendKind.Word, +1, default));
        Assert.Equal(1, elsewhere.PageNeeded);
    }

    [Fact]
    public void LinesMoveAcrossPagesLandingOnTheNearEdge()
    {
        var selection = new SelectionController(Two());
        selection.Select(new TextPosition(0, 13), new TextPosition(0, 13)); // on the second (last) line of page 1
        Assert.Equal(ExtendOutcome.Moved, selection.Extend(ExtendKind.Line, +1, default));
        Assert.Equal(1, selection.Selection.Active.Page);
        Assert.Equal(ExtendOutcome.Moved, selection.Extend(ExtendKind.Line, -1, default));
        Assert.Equal(0, selection.Selection.Active.Page);
        selection.Select(new TextPosition(0, 2), new TextPosition(0, 2));
        Assert.Equal(ExtendOutcome.AtEdge, selection.Extend(ExtendKind.Line, -1, default));
    }

    [Fact]
    public async Task TheTextOfASelectionIsCollectedPageByPageWithBreaksBetweenParagraphsAndPages()
    {
        var pages = Two();
        var selection = new TextSelection(new TextPosition(0, 6), new TextPosition(1, 7));
        var (text, truncated) = await SelectionText.CollectAsync(selection, (page, _) => Task.FromResult(pages.GetLoaded(page)), "\r\n", CancellationToken.None);
        Assert.False(truncated);
        Assert.Equal("beta gamma delta\r\n\r\nepsilon", text);

        var (all, _) = await SelectionText.CollectAsync(new TextSelection(TextPosition.StartOf(0), TextPosition.EndOf(1)), (page, _) => Task.FromResult(pages.GetLoaded(page)), "\n", CancellationToken.None);
        Assert.Equal("alpha beta gamma delta\n\nepsilon zeta", all);

        var empty = await SelectionText.CollectAsync(default, (_, _) => Task.FromResult<TextModel?>(null), "\n", CancellationToken.None);
        Assert.Equal((string.Empty, false), empty);
    }

    [Fact]
    public async Task ParagraphBreaksBecomeTheRequestedNewLineAndPagesThatCannotBeReadAreSkipped()
    {
        var model = Page(0, new Run("one", 72, 72), new Run("two", 72, 150));
        Assert.Equal("one\ntwo", model.Text);
        var (text, _) = await SelectionText.CollectAsync(
            new TextSelection(TextPosition.StartOf(0), TextPosition.EndOf(2)),
            (page, _) => Task.FromResult(page == 0 ? model : null),
            "\r\n",
            CancellationToken.None);
        Assert.Equal("one\r\ntwo", text);
    }

    [Fact]
    public async Task CollectingStopsAtTheLimitAndSaysSo()
    {
        var pages = Two();
        var (text, truncated) = await SelectionText.CollectAsync(
            new TextSelection(TextPosition.StartOf(0), TextPosition.EndOf(1)),
            (page, _) => Task.FromResult(pages.GetLoaded(page)),
            "\n",
            CancellationToken.None,
            maxCharacters: 8);
        Assert.True(truncated);
        Assert.Equal("alpha be", text);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SelectionText.CollectAsync(
            new TextSelection(TextPosition.StartOf(0), TextPosition.EndOf(1)), (page, _) => Task.FromResult(pages.GetLoaded(page)), "\n", cancelled.Token));
    }

    [Fact]
    public void ASelectionHasOneBoxPerLineOnEachPage()
    {
        var pages = Two();
        var selection = new TextSelection(new TextPosition(0, 6), new TextPosition(1, 7));
        var first = SelectionText.RectsOnPage(selection, 0, pages.GetLoaded(0)!);
        Assert.Equal(2, first.Count);
        Assert.Equal(72 + (6 * Advance), first[0].X, 3);
        var second = SelectionText.RectsOnPage(selection, 1, pages.GetLoaded(1)!);
        Assert.Single(second);
        Assert.Equal(7 * Advance, second[0].Width, 3);
        Assert.Empty(SelectionText.RectsOnPage(selection, 2, pages.GetLoaded(1)!));
        Assert.Empty(SelectionText.RectsOnPage(default, 0, pages.GetLoaded(0)!));
        _ = new PointD(0, 0);
    }
}
