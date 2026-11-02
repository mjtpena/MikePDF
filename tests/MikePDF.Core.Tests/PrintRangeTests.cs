using MikePDF.Core.Printing;

namespace MikePDF.Core.Tests;

/// <summary>print.md 3.3 and criteria PRINT-01 and PRINT-02: the range grammar, its errors, and how the pages are chosen.</summary>
public sealed class PrintRangeTests
{
    private static int[] Pages(string text, int pageCount = 10)
    {
        var result = PageRangeParser.Parse(text, pageCount);
        Assert.True(result.IsValid, $"'{text}' was refused: {result.Error}");
        return [.. result.Pages];
    }

    [Theory]
    [InlineData("1", new[] { 1 })]
    [InlineData("1-3", new[] { 1, 2, 3 })]
    [InlineData("5-", new[] { 5, 6, 7, 8, 9, 10 })]
    [InlineData("-5", new[] { 1, 2, 3, 4, 5 })]
    [InlineData("1-3,5,8-", new[] { 1, 2, 3, 5, 8, 9, 10 })]
    [InlineData(" 1 - 3 , 5 ,\t8- ", new[] { 1, 2, 3, 5, 8, 9, 10 })]
    [InlineData("10", new[] { 10 })]
    [InlineData("1-10", new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 })]
    public void TheGrammarOfThe33ParsesToAscendingPages(string text, int[] expected) => Assert.Equal(expected, Pages(text));

    [Fact]
    public void OverlapsAndRepeatsPrintAPageOnce()
    {
        Assert.Equal([1, 2, 3, 4, 5], Pages("1-3, 2-5"));
        Assert.Equal([2, 3], Pages("2,2,3,3"));
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9, 10], Pages("1-4,3-,5-10"));
    }

    [Theory]
    [InlineData("", PageRangeError.Empty, "Print_Range_Error_Syntax")]
    [InlineData("   ", PageRangeError.Empty, "Print_Range_Error_Syntax")]
    [InlineData("a", PageRangeError.Syntax, "Print_Range_Error_Syntax")]
    [InlineData("1-a", PageRangeError.Syntax, "Print_Range_Error_Syntax")]
    [InlineData("0", PageRangeError.Syntax, "Print_Range_Error_Syntax")]
    [InlineData("0-3", PageRangeError.Syntax, "Print_Range_Error_Syntax")]
    [InlineData("-0", PageRangeError.Syntax, "Print_Range_Error_Syntax")]
    [InlineData("-", PageRangeError.Syntax, "Print_Range_Error_Syntax")]
    [InlineData("1,,2", PageRangeError.Syntax, "Print_Range_Error_Syntax")]
    [InlineData("1,", PageRangeError.Syntax, "Print_Range_Error_Syntax")]
    [InlineData(",1", PageRangeError.Syntax, "Print_Range_Error_Syntax")]
    [InlineData("1-2-3", PageRangeError.Syntax, "Print_Range_Error_Syntax")]
    [InlineData("+1", PageRangeError.Syntax, "Print_Range_Error_Syntax")]
    [InlineData("1.5", PageRangeError.Syntax, "Print_Range_Error_Syntax")]
    [InlineData("１", PageRangeError.Syntax, "Print_Range_Error_Syntax")] // a full-width digit one: the grammar is ASCII
    [InlineData("3-1", PageRangeError.OutOfOrder, "Print_Range_Error_Order")]
    [InlineData("5,1", PageRangeError.OutOfOrder, "Print_Range_Error_Order")]
    [InlineData("5-,1", PageRangeError.OutOfOrder, "Print_Range_Error_Order")]
    [InlineData("11", PageRangeError.OutOfRange, "Print_Range_Error_Limit")]
    [InlineData("1-11", PageRangeError.OutOfRange, "Print_Range_Error_Limit")]
    [InlineData("11-", PageRangeError.OutOfRange, "Print_Range_Error_Limit")]
    [InlineData("-11", PageRangeError.OutOfRange, "Print_Range_Error_Limit")]
    [InlineData("99999999999999999999", PageRangeError.OutOfRange, "Print_Range_Error_Limit")]
    public void ABadRangeIsRefusedWithTheRightErrorAndKey(string text, PageRangeError error, string key)
    {
        var result = PageRangeParser.Parse(text, 10);
        Assert.False(result.IsValid);
        Assert.Equal(error, result.Error);
        Assert.Equal(key, result.ResourceKey);
        Assert.True(result.Pages.IsEmpty);
    }

    [Fact]
    public void WhitespaceInsideANumberIsIgnoredLikeEverywhereElse()
    {
        // "1 2" is page 12 (the grammar ignores whitespace): past the end of a ten page document, fine in a twenty page one.
        Assert.Equal(PageRangeError.OutOfRange, PageRangeParser.Parse("1 2", 10).Error);
        Assert.Equal([12], Pages("1 2", 20));
    }

    [Fact]
    public void TheLimitErrorNamesThePageCount()
    {
        var result = PageRangeParser.Parse("41", 40);
        Assert.Equal(PageRangeError.OutOfRange, result.Error);
        Assert.Equal(40, result.Limit);
        Assert.Equal(PrintStrings.RangeErrorLimit, result.ResourceKey);
    }

    [Fact]
    public void MoreThan256CharactersIsRefusedAndExactly256IsNot()
    {
        var tooLong = PageRangeParser.Parse(new string(' ', 257) + "1", 10);
        Assert.Equal(PageRangeError.TooLong, tooLong.Error);
        Assert.Equal("Print_Range_Error_Syntax", tooLong.ResourceKey);

        var exact = PageRangeParser.Parse(new string(' ', 255) + "1", 10);
        Assert.True(exact.IsValid);
        Assert.Equal(256, PageRangeParser.MaxLength);
    }

    [Fact]
    public void MoreThan4096ItemsIsRefused()
    {
        // 256 characters allow 128 items at most, so the item limit is checked with the limits lowered, which is what the internal overload is for.
        var text = string.Join(',', Enumerable.Repeat("1", 5));
        Assert.Equal(PageRangeError.TooManyItems, PageRangeParser.Parse(text, 10, 256, 4).Error);
        Assert.True(PageRangeParser.Parse(text, 10, 256, 5).IsValid);
        Assert.Equal(4096, PageRangeParser.MaxItems);
        var longest = string.Join(',', Enumerable.Repeat("1", 128)); // 255 characters
        Assert.True(PageRangeParser.Parse(longest, 10).IsValid);
    }

    [Fact]
    public void NullAndAnEmptyDocumentAreRefused()
    {
        Assert.Equal(PageRangeError.Empty, PageRangeParser.Parse(null, 10).Error);
        Assert.Equal(PageRangeError.OutOfRange, PageRangeParser.Parse("1", 0).Error);
    }

    [Fact]
    public void AHugeDocumentCostsRunsAndNotPages()
    {
        var result = PageRangeParser.Parse("2-", 10_000_000);
        Assert.True(result.IsValid);
        Assert.Equal(9_999_999, result.Pages.Count);
        Assert.Single(result.Pages.Runs);
        Assert.Equal(2, result.Pages[0]);
        Assert.Equal(10_000_000, result.Pages[9_999_998]);
    }

    // ---- PRINT-02: expansion, odd/even, the other choices ----

    [Fact]
    public void FiveDashOnATenPageDocumentGivesFiveToTen()
    {
        var result = PageSelection.Resolve(PrintScope.Range, 10, 1, "5-", null, PageSubset.All);
        Assert.Equal([5, 6, 7, 8, 9, 10], result.Pages);
    }

    [Fact]
    public void OddAndEvenApplyAfterTheRange()
    {
        Assert.Equal([3, 5, 7, 9], PageSelection.Resolve(PrintScope.Range, 12, 1, "2-9", null, PageSubset.Odd).Pages);
        Assert.Equal([2, 4, 6, 8], PageSelection.Resolve(PrintScope.Range, 12, 1, "2-9", null, PageSubset.Even).Pages);
        Assert.Equal([1, 3, 5, 7, 9, 11], PageSelection.Resolve(PrintScope.AllPages, 12, 1, null, null, PageSubset.Odd).Pages);
        Assert.Equal([2, 4, 6, 8, 10, 12], PageSelection.Resolve(PrintScope.AllPages, 12, 1, null, null, PageSubset.Even).Pages);
    }

    [Fact]
    public void OddEvenOverSeveralRunsKeepsEachRunsParity()
    {
        // 1-4 then 7: odd gives 1, 3 and 7; even gives 2 and 4.
        Assert.Equal([1, 3, 7], PageSelection.Resolve(PrintScope.Range, 12, 1, "1-4, 7", null, PageSubset.Odd).Pages);
        Assert.Equal([2, 4], PageSelection.Resolve(PrintScope.Range, 12, 1, "1-4, 7", null, PageSubset.Even).Pages);
        // Odd of an even-sized run: 4-7 gives 5 and 7.
        Assert.Equal([5, 7], PageSelection.Resolve(PrintScope.Range, 12, 1, "4-7", null, PageSubset.Odd).Pages);
        Assert.Equal(2, PageSelection.Resolve(PrintScope.Range, 12, 1, "4-7", null, PageSubset.Odd).Pages.Count);
    }

    [Fact]
    public void CurrentPageAndSelectedPagesAreChosenWithoutAnyRangeText()
    {
        Assert.Equal([4], PageSelection.Resolve(PrintScope.CurrentPage, 10, 4, "junk", null, PageSubset.All).Pages);
        Assert.Equal([2, 3, 4, 9], PageSelection.Resolve(PrintScope.SelectedPages, 10, 1, null, (int[])[9, 3, 2, 4, 3], PageSubset.All).Pages);
        Assert.Equal(PageRangeError.OutOfRange, PageSelection.Resolve(PrintScope.CurrentPage, 10, 11, null, null, PageSubset.All).Error);
        Assert.Equal(PageRangeError.OutOfRange, PageSelection.Resolve(PrintScope.SelectedPages, 10, 1, null, (int[])[11], PageSubset.All).Error);
    }

    [Fact]
    public void AChoiceThatLeavesNoPageIsAnErrorWithItsOwnKey()
    {
        var nothing = PageSelection.Resolve(PrintScope.CurrentPage, 10, 2, null, null, PageSubset.Odd);
        Assert.Equal(PageRangeError.NoPages, nothing.Error);
        Assert.Equal(PrintStrings.RangeErrorNone, nothing.ResourceKey);
        Assert.Equal(PageRangeError.NoPages, PageSelection.Resolve(PrintScope.SelectedPages, 10, 1, null, [], PageSubset.All).Error);
        Assert.Equal(PageRangeError.NoPages, PageSelection.Resolve(PrintScope.AllPages, 0, 1, null, null, PageSubset.All).Error);
    }

    [Fact]
    public void ARangeErrorCarriesThroughTheSelection()
    {
        var result = PageSelection.Resolve(PrintScope.Range, 10, 1, "3-1", null, PageSubset.Odd);
        Assert.Equal(PageRangeError.OutOfOrder, result.Error);
        Assert.Equal(PrintStrings.RangeErrorOrder, result.ResourceKey);
    }

    [Fact]
    public void ThePageSetIndexesAndContainsLikeTheList()
    {
        var pages = PageSelection.Resolve(PrintScope.Range, 20, 1, "2-6, 10, 15-", null, PageSubset.All).Pages;
        var list = pages.ToList();
        Assert.Equal(list.Count, pages.Count);
        for (var i = 0; i < list.Count; i++)
        {
            Assert.Equal(list[i], pages[i]);
            Assert.True(pages.Contains(list[i]));
        }

        Assert.False(pages.Contains(7));
        Assert.False(pages.Contains(0));
        Assert.False(pages.Contains(21));
        Assert.Throws<ArgumentOutOfRangeException>(() => pages[list.Count]);
        var odd = pages.Filter(PageSubset.Odd);
        Assert.Equal(list.Where(p => p % 2 == 1), odd);
        Assert.True(odd.Contains(3) && !odd.Contains(4));
    }

    [Fact]
    public void EveryPageOfEverySmallRangeAppearsOnceInOrder()
    {
        // A cheap exhaustive check of the grammar against a brute-force expansion: every "a-b" and "a," pair on a 9 page document.
        for (var a = 1; a <= 9; a++)
        {
            for (var b = a; b <= 9; b++)
            {
                Assert.Equal(Enumerable.Range(a, b - a + 1), Pages($"{a}-{b}", 9));
            }

            Assert.Equal(Enumerable.Range(a, 10 - a), Pages($"{a}-", 9));
            Assert.Equal(Enumerable.Range(1, a), Pages($"-{a}", 9));
        }
    }
}
