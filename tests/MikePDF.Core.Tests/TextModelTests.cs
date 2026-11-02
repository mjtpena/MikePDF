using MikePDF.Core.Geometry;
using MikePDF.Core.Text;
using static MikePDF.Core.Tests.TextFixtures;

namespace MikePDF.Core.Tests;

public sealed class TextModelTests
{
    [Fact]
    public void LinesOfOneColumnAreReadTopToBottomAndJoinedWithSpaces()
    {
        // Content order is not top to bottom.
        var model = Model(new Run("third", 72, 104), new Run("Hello world", 72, 72), new Run("second line", 72, 88));
        Assert.Equal("Hello world second line third", model.Text);
        Assert.Equal(3, model.Boundaries(TextUnitKind.Line).Length - 1);
        Assert.Single(model.Boundaries(TextUnitKind.Paragraph), 0);
    }

    [Fact]
    public void TwoColumnsAreReadOneAfterTheOtherNotRowByRow()
    {
        // The content stream interleaves the columns, as PDFium's own text order would show.
        var model = Model(
            new Run("left one", 72, 72), new Run("right one", 330, 72),
            new Run("left two", 72, 88), new Run("right two", 330, 88),
            new Run("left three", 72, 104), new Run("right three", 330, 104));
        Assert.Equal("left one left two left three" + "\n" + "right one right two right three", model.Text);
    }

    [Fact]
    public void AFullWidthTitleComesBeforeTheColumnsUnderIt()
    {
        const string Title = "The Title Of It All Runs Right Across Both Of The Columns Below";
        var model = Model(
            new Run("alpha body", 72, 120), new Run("beta body", 330, 120),
            new Run(Title, 72, 60),
            new Run("alpha more", 72, 136), new Run("beta more", 330, 136));
        Assert.Equal(Title + "\n" + "alpha body alpha more" + "\n" + "beta body beta more", model.Text);
    }

    [Fact]
    public void ALargerGapStartsAParagraph()
    {
        var model = Model(new Run("one", 72, 72), new Run("two", 72, 88), new Run("three", 72, 104), new Run("four", 72, 160), new Run("five", 72, 176));
        Assert.Equal("one two three\nfour five", model.Text);
        Assert.Equal(2, model.Boundaries(TextUnitKind.Paragraph).Length - 1);
    }

    [Fact]
    public void PdfiumsMadeUpLineBreaksAreReplacedByTheModelsOwnSeparators()
    {
        var model = TextModelBuilder.Build(Chars(true, new Run("first line", 72, 72), new Run("second line", 72, 88)), Letter, 0);
        Assert.Equal("first line second line", model.Text);
    }

    [Fact]
    public void AGeneratedSpaceIsKeptAndOtherControlCharactersAreNot()
    {
        var chars = Chars(new Run("a", 72, 72), new Run(" ", 78, 72, PageCharacters.Generated), new Run("b", 84, 72), new Run("\u0007", 90, 72), new Run("c", 96, 72));
        Assert.Equal("a bc", TextModelBuilder.Build(chars, Letter, 0).Text);
    }

    [Fact]
    public void ALineEndHyphenIsDroppedAndTheWordIsOneAcrossTwoLines()
    {
        var model = Model(new Run("an exam", 72, 72), new Run("\u0002", 114, 72, PageCharacters.Hyphen), new Run("ple of it", 72, 88));
        Assert.Equal("an example of it", model.Text);
        var word = model.Boundaries(TextUnitKind.Word);
        // "example" is a single word although it spans two lines.
        var start = model.Text.IndexOf("example", StringComparison.Ordinal);
        var unit = model.UnitIndex(TextUnitKind.Word, start);
        Assert.Equal("example ", model.GetText(word[unit], word[unit + 1]));
        Assert.Equal(2, model.Boundaries(TextUnitKind.Line).Length - 1);
    }

    [Fact]
    public void ALigatureBecomesLettersWhoseBoxesShareTheGlyph()
    {
        var chars = Chars(new Run("" + (char)0xFB01 + "x", 72, 72));
        var model = TextModelBuilder.Build(chars, Letter, 0);
        Assert.Equal("fix", model.Text);
        var f = model.Atoms[0].Rect!.Value;
        var i = model.Atoms[1].Rect!.Value;
        Assert.Equal(f.Right, i.X, 3);
        Assert.Equal(6, f.Width + i.Width, 3);
    }

    [Fact]
    public void ASurrogatePairIsOneAtomAndALoneSurrogateIsReplaced()
    {
        var chars = new PageCharacters(
            [0xD83D, 0xDE00, 'a', 0xD800, 'b'],
            [72, 72, 84, 90, 96],
            [700, 700, 700, 700, 700],
            [84, 84, 90, 96, 102],
            [712, 712, 712, 712, 712],
            [0, 0, 0, 0, 0],
            false);
        var model = TextModelBuilder.Build(chars, Letter, 0);
        Assert.Equal("\U0001F600a" + (char)0xFFFD + "b", model.Text);
        Assert.Equal(4, model.Length);
        Assert.Equal(2, model.Atoms[0].Text.Length);
    }

    [Fact]
    public void CharactersOutsideThePageAreIgnored()
    {
        var model = Model(new Run("inside", 72, 72), new Run("outside", -500, 72), new Run("below", 72, 900));
        Assert.Equal("inside", model.Text);
    }

    [Fact]
    public void ARotatedPageIsReadAsItIsSeenAndTheBoxesAreInPageSpace()
    {
        var rotated = new PageGeometry(new PdfRect(0, 0, 612, 792), null, 90);
        var chars = Chars(new Run("hi", 72, 72));
        var model = TextModelBuilder.Build(chars, rotated, 4);
        // The line runs down the page as it is seen (the text was not authored to be upright), so each letter is a line of its own.
        Assert.Equal("h i", model.Text);
        Assert.All(model.Atoms, a => Assert.Equal(4, a.Page));
        foreach (var atom in model.Atoms.Where(a => a.Source >= 0))
        {
            var expected = rotated.PdfToPage(chars.BoxOf(atom.Source));
            Assert.Equal(expected.X, atom.Rect!.Value.X, 3);
            Assert.Equal(expected.Y, atom.Rect!.Value.Y, 3);
        }
    }

    [Fact]
    public void ABlankPageHasNoTextAndNothingToHit()
    {
        var model = TextModelBuilder.Build(PageCharacters.Empty, Letter, 0);
        Assert.Equal(0, model.Length);
        Assert.Equal(-1, model.HitTest(0, new PointD(10, 10)));
        Assert.Empty(model.LineRects(0, 10));
        Assert.Equal([0], model.Boundaries(TextUnitKind.Word));
        Assert.Equal((0, 0), model.PageRange(0));
    }

    [Fact]
    public void HitTestingFindsTheCharacterUnderThePointAndTheNearestOneAroundIt()
    {
        var model = Model(new Run("Hello", 72, 72), new Run("World", 72, 88));
        // 'e' of Hello: x 78..84, y 72..84 in page space.
        Assert.Equal('e', model.Atoms[model.HitTest(0, new PointD(80, 78))].Text[0]);
        // Left of the line, in the margin: the first character of the nearest line.
        Assert.Equal('W', model.Atoms[model.HitTest(0, new PointD(10, 94))].Text[0]);
        // Far right of a line: its last character.
        Assert.Equal('o', model.Atoms[model.HitTest(0, new PointD(500, 78))].Text[0]);
        // Above everything: the first line.
        Assert.Equal('H', model.Atoms[model.HitTest(0, new PointD(72, 0))].Text[0]);
        // A page the model does not hold.
        Assert.Equal(-1, model.HitTest(3, new PointD(80, 78)));
    }

    [Fact]
    public void LineRectsGiveOneBoxPerLine()
    {
        var model = Model(new Run("Hello", 72, 72), new Run("World", 72, 88));
        var all = model.LineRects(0, model.Length).ToList();
        Assert.Equal(2, all.Count);
        Assert.Equal(72, all[0].Rect.X, 3);
        Assert.Equal(30, all[0].Rect.Width, 3);
        Assert.Equal(88, all[1].Rect.Y, 3);
        var part = model.LineRects(2, 8).ToList();   // "llo Wo"
        Assert.Equal(2, part.Count);
        Assert.Equal(84, part[0].Rect.X, 3);
    }

    [Fact]
    public void TextCanBeAddressedByCharacterOffsetAsWellAsByAtom()
    {
        var chars = new PageCharacters(
            new ushort[] { 'a', 0xD83D, 0xDE00, 'b' },
            [72, 78, 78, 90], [700, 700, 700, 700], [78, 90, 90, 96], [712, 712, 712, 712], [0, 0, 0, 0], false);
        var model = TextModelBuilder.Build(chars, Letter, 0);
        Assert.Equal("a\U0001F600b", model.Text);
        Assert.Equal(0, model.AtomAtOffset(0));
        Assert.Equal(1, model.AtomAtOffset(1));
        Assert.Equal(1, model.AtomAtOffset(2));   // inside the pair: the pair
        Assert.Equal(2, model.AtomAtOffset(3));
        Assert.Equal(3, model.AtomAtOffset(99));
        Assert.Equal(3, model.OffsetOfAtom(2));
        Assert.Equal(0, model.AtomAtOffset(-4));
        Assert.Equal("\U0001F600", model.GetText(1, 2));
    }

    [Fact]
    public void PagesCombineWithALineBreakBetweenThemAndKeepTheirOwnNumbers()
    {
        var one = TextModelBuilder.Build(Chars(new Run("first page", 72, 72)), Letter, 3);
        var two = TextModelBuilder.Build(Chars(new Run("second page", 72, 72)), Letter, 4);
        var both = TextModel.Combine([one, TextModel.Empty, two]);
        Assert.Equal("first page\nsecond page", both.Text);
        Assert.Equal([3, 4], both.Pages.ToArray());
        Assert.Equal((0, 10), both.PageRange(3));
        Assert.Equal((11, 22), both.PageRange(4));
        Assert.Equal((0, 0), both.PageRange(5));
        Assert.Equal(3, both.Boundaries(TextUnitKind.Page).Length);
        var lines = both.LineRects(0, both.Length).ToList();
        Assert.Equal([3, 4], lines.Select(l => l.Page).ToArray());
        Assert.Equal('s', both.Atoms[both.HitTest(4, new PointD(73, 78))].Text[0]);
        Assert.Equal(2, both.Boundaries(TextUnitKind.Line).Length - 1);
        Assert.Equal(2, both.Boundaries(TextUnitKind.Paragraph).Length - 1);
    }

    [Fact]
    public void WordLineAndCharacterBoundariesPartitionTheText()
    {
        var model = Model(new Run("one two", 72, 72), new Run("three four", 72, 88));
        foreach (var unit in new[] { TextUnitKind.Character, TextUnitKind.Word, TextUnitKind.Line, TextUnitKind.Paragraph, TextUnitKind.Page, TextUnitKind.Document })
        {
            var bounds = model.Boundaries(unit);
            Assert.Equal(0, bounds[0]);
            Assert.Equal(model.Length, bounds[^1]);
            Assert.Equal(bounds.OrderBy(b => b).ToArray(), bounds);
            Assert.Equal(model.Text, string.Concat(Enumerable.Range(0, bounds.Length - 1).Select(i => model.GetText(bounds[i], bounds[i + 1]))));
        }

        Assert.Equal("one two three four", model.Text);
    }

    [Fact]
    public void LineOfFindsTheLineOfAnAtomAndNothingForASeparatorOnlyLine()
    {
        var model = Model(new Run("ab", 72, 72), new Run("cd", 72, 88));
        Assert.Equal(0, model.LineOf(0));
        Assert.Equal(1, model.LineOf(3));
        Assert.Equal(-1, model.LineOf(99));
        Assert.Equal(2, model.LineCount);
        Assert.Equal((0, 3, 2), (model.LineAt(0).Start, model.LineAt(0).End, 2));
        Assert.Equal(model.LineAt(1).Start, model.AtomInLineNear(1, 0));
    }
}
