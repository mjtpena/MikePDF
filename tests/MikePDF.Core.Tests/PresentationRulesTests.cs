using MikePDF.Core.Windowing;

namespace MikePDF.Core.Tests;

/// <summary>The rules of presentation mode that need no window (docs/features/page-view-modes.md 3.6, 6; PV-21).</summary>
public sealed class PresentationRulesTests
{
    [Fact]
    public void PV21_ThePresentationEndsWhenTheDisplayItStartedOnIsGone()
    {
        Assert.True(PresentationRules.ShouldEndOnDisplayChange("2", ["1"]));
        Assert.True(PresentationRules.ShouldEndOnDisplayChange("2", ["1", "3", "4"]));
        Assert.True(PresentationRules.ShouldEndOnDisplayChange("65537", ["65538"]));
    }

    [Fact]
    public void PV21_ThePresentationGoesOnWhenItsDisplayIsStillThereWhateverElseChanged()
    {
        Assert.False(PresentationRules.ShouldEndOnDisplayChange("2", ["2"]));
        Assert.False(PresentationRules.ShouldEndOnDisplayChange("2", ["1", "2"]));   // another display was added
        Assert.False(PresentationRules.ShouldEndOnDisplayChange("2", ["2", "9"]));   // or one of the others went
    }

    [Fact]
    public void PV21_WhenWindowsGivesNoListNothingIsKnownSoThePresentationGoesOn()
    {
        Assert.False(PresentationRules.ShouldEndOnDisplayChange("2", []));
        Assert.False(PresentationRules.ShouldEndOnDisplayChange(string.Empty, []));
    }

    [Fact]
    public void PV21_DisplayNamesAreComparedExactly()
    {
        Assert.True(PresentationRules.ShouldEndOnDisplayChange("2", ["02"]));
        Assert.True(PresentationRules.ShouldEndOnDisplayChange("A", ["a"]));
        Assert.Throws<ArgumentNullException>(() => PresentationRules.ShouldEndOnDisplayChange("2", null!));
    }

    [Theory]
    [InlineData("1", 10, 0)]
    [InlineData("5", 10, 4)]
    [InlineData("10", 10, 9)]
    [InlineData("11", 10, 9)]     // past the end: the last page
    [InlineData("0", 10, 0)]      // there is no page 0: the first
    [InlineData("99999999", 10, 9)]
    public void PV15_ATypedNumberIsAOneBasedPageKeptInsideTheDocument(string typed, int pages, int expected) =>
        Assert.Equal(expected, PresentationRules.PageFromTyped(typed, pages));

    [Theory]
    [InlineData("", 10)]
    [InlineData("12a", 10)]
    [InlineData("-3", 10)]
    [InlineData("1 2", 10)]
    [InlineData("123456789", 10)]  // nine digits: not a page number
    [InlineData("5", 0)]
    [InlineData("٥", 10)]     // an Arabic-Indic digit is not one of ours
    public void PV15_TypedTextThatIsNotAPageNumberNamesNoPage(string typed, int pages) =>
        Assert.Null(PresentationRules.PageFromTyped(typed, pages));
}
