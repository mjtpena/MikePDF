using MikePDF.Core.Text;
using static MikePDF.Core.Tests.TextFixtures;

namespace MikePDF.Core.Tests;

public sealed class TextRangeTests
{
    // "alpha beta gamma" / "delta epsilon" / (new paragraph) "zeta"
    private static TextModel Sample() => Model(
        new Run("alpha beta gamma", 72, 72),
        new Run("delta epsilon", 72, 88),
        new Run("zeta", 72, 150));

    [Fact]
    public void ExpandingToAWordGivesTheWordAndItsTrailingSpace()
    {
        var model = Sample();
        var range = new TextRange(model, 8, 8); // inside "beta"
        range.ExpandToEnclosingUnit(TextUnitKind.Word);
        Assert.Equal("beta ", range.GetText());
        range.ExpandToEnclosingUnit(TextUnitKind.Line);
        Assert.Equal("alpha beta gamma ", range.GetText());
        range.ExpandToEnclosingUnit(TextUnitKind.Paragraph);
        Assert.StartsWith("alpha beta gamma delta epsilon", range.GetText(), StringComparison.Ordinal);
        range.ExpandToEnclosingUnit(TextUnitKind.Document);
        Assert.Equal(model.Text, range.GetText());
        range.ExpandToEnclosingUnit(TextUnitKind.Character);
        Assert.Equal(1, range.End - range.Start);
    }

    [Fact]
    public void TheEndOfTheDocumentExpandsToTheLastUnit()
    {
        var model = Sample();
        var range = new TextRange(model, model.Length, model.Length);
        range.ExpandToEnclosingUnit(TextUnitKind.Word);
        Assert.Equal("zeta", range.GetText());
    }

    [Fact]
    public void MovingByWordsWalksTheWholeTextForwardAndBack()
    {
        var model = Sample();
        var words = new List<string>();
        var range = new TextRange(model, 0, 0);
        range.ExpandToEnclosingUnit(TextUnitKind.Word);
        do
        {
            words.Add(range.GetText().Trim());
        }
        while (range.Move(TextUnitKind.Word, 1) == 1);

        Assert.Equal(["alpha", "beta", "gamma", "delta", "epsilon", "zeta"], words);
        Assert.Equal(0, range.Move(TextUnitKind.Word, 1)); // at the last one
        Assert.Equal(-5, range.Move(TextUnitKind.Word, -5));
        Assert.Equal("alpha", range.GetText().Trim());
        Assert.Equal(0, range.Move(TextUnitKind.Word, 0));
    }

    [Fact]
    public void ADegenerateRangeMovesAsAPointWithinUnits()
    {
        var model = Sample();
        var point = new TextRange(model, 8, 8);
        Assert.Equal(1, point.Move(TextUnitKind.Word, 1));   // from inside "beta" to the start of "gamma"
        Assert.Equal(11, point.Start);
        Assert.True(point.IsDegenerate);
        Assert.Equal(-1, point.Move(TextUnitKind.Word, -1)); // back to the start of "beta"
        Assert.Equal(6, point.Start);
        var far = new TextRange(model, 7, 7);
        Assert.Equal(-2, far.Move(TextUnitKind.Word, int.MinValue)); // no overflow: the start of "beta", then the start of "alpha"
        Assert.Equal(0, far.Start);
    }

    [Fact]
    public void MovingEndpointsGrowsAndShrinksTheRangeAndDragsTheOtherEndAlongWhenTheyCross()
    {
        var model = Sample();
        var range = new TextRange(model, 6, 10); // "beta"
        Assert.Equal(1, range.MoveEndpointByUnit(RangeEndpoint.End, TextUnitKind.Word, 1));
        Assert.Equal("beta ", range.GetText());
        Assert.Equal(1, range.MoveEndpointByUnit(RangeEndpoint.End, TextUnitKind.Word, 1));
        Assert.Equal("beta gamma ", range.GetText());
        Assert.Equal(-2, range.MoveEndpointByUnit(RangeEndpoint.End, TextUnitKind.Word, -2));
        Assert.True(range.Start <= range.End);
        range.Select(6, 10);
        range.MoveEndpointByUnit(RangeEndpoint.Start, TextUnitKind.Character, 9);
        Assert.Equal(range.Start, range.End); // start passed the end, the end followed
        Assert.Equal(0, range.MoveEndpointByUnit(RangeEndpoint.Start, TextUnitKind.Word, 0));
    }

    [Fact]
    public void RangesCompareByEndpointsAndCanTakeEndpointsFromEachOther()
    {
        var model = Sample();
        var a = new TextRange(model, 0, 5);
        var b = new TextRange(model, 6, 10);
        Assert.False(a.Compare(b));
        Assert.True(a.Compare(a.Clone()));
        Assert.True(a.CompareEndpoints(RangeEndpoint.End, b, RangeEndpoint.Start) < 0);
        Assert.True(a.CompareEndpoints(RangeEndpoint.Start, b, RangeEndpoint.Start) < 0);
        a.MoveEndpointByRange(RangeEndpoint.End, b, RangeEndpoint.End);
        Assert.Equal(10, a.End);
        a.MoveEndpointByRange(RangeEndpoint.Start, b, RangeEndpoint.End);
        Assert.True(a.IsDegenerate);
        Assert.Equal("alpha beta", new TextRange(model, 0, 10).GetText());
        Assert.Equal("alp", new TextRange(model, 0, 10).GetText(3));
    }

    [Fact]
    public void ARangeIsAlwaysInsideTheModelAndInOrder()
    {
        var model = Sample();
        var range = new TextRange(model, -5, 9999);
        Assert.Equal((0, model.Length), (range.Start, range.End));
        range.Select(30, 10);
        Assert.True(range.Start <= range.End);
        var empty = new TextRange(TextModel.Empty, 3, 4);
        empty.ExpandToEnclosingUnit(TextUnitKind.Word);
        Assert.Equal(0, empty.Move(TextUnitKind.Line, 3));
        Assert.Equal(string.Empty, empty.GetText());
        Assert.Throws<ArgumentNullException>(() => new TextRange(null!, 0, 0));
    }
}
