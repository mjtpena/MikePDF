using System.Text.RegularExpressions;

namespace MikePDF.Core.Tests;

/// <summary>
/// Checks, from the source of the print sheet, what the UI journeys cannot reach on a machine where system settings may not be changed: the sheet uses the system's colours (so high contrast
/// works), every control of the design has its automation id (design 11-print section 6), and the tab order is the document order. The journeys check the same things live where they can.
/// </summary>
public sealed class PrintSheetSourceTests
{
    private static string App(params string[] parts) => Path.Combine([ResourceText.RepoRoot(), "src", "MikePDF.App", .. parts]);

    private static string SheetXaml => File.ReadAllText(App("Views", "PrintSheetView.xaml"));

    private static string SheetCode => File.ReadAllText(App("Views", "PrintSheetView.xaml.cs"));

    private static string WindowXaml => File.ReadAllText(App("MainWindow.xaml"));

    [Fact]
    public void TheSheetAndTheStripNameNoColourOfTheirOwnSoHighContrastAndThemesApplyToThem()
    {
        foreach (var text in new[] { SheetXaml, SheetCode, File.ReadAllText(App("MainWindow.Print.cs")) })
        {
            Assert.DoesNotMatch(@"=""#[0-9A-Fa-f]{3,8}""", text);
            Assert.DoesNotContain("Colors.", text, StringComparison.Ordinal);
            Assert.DoesNotContain("new SolidColorBrush", text, StringComparison.Ordinal);
        }

        // The brushes it does use are theme resources: they change with the theme and with high contrast.
        var brushes = Regex.Matches(SheetXaml, @"\{ThemeResource (\w+)\}").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.NotEmpty(brushes);
        Assert.All(brushes, b => Assert.True(b.EndsWith("Brush", StringComparison.Ordinal) || b.EndsWith("Style", StringComparison.Ordinal), b));
    }

    [Theory]
    [InlineData("PrintSheet")]
    [InlineData("PrintPrinter")]
    [InlineData("PrintPreferences")]
    [InlineData("PrintCopies")]
    [InlineData("PrintCollate")]
    [InlineData("PrintPagesAll")]
    [InlineData("PrintPagesCurrent")]
    [InlineData("PrintPagesRange")]
    [InlineData("PrintRange")]
    [InlineData("PrintRangeError")]
    [InlineData("PrintSubset")]
    [InlineData("PrintSides")]
    [InlineData("PrintOrientation")]
    [InlineData("PrintAutoRotate")]
    [InlineData("PrintScale")]
    [InlineData("PrintScaleCustom")]
    [InlineData("PrintPagesPerSheet")]
    [InlineData("PrintSheetOrder")]
    [InlineData("PrintPaper")]
    [InlineData("PrintColour")]
    [InlineData("PrintIncludeAnnotations")]
    [InlineData("PrintIncludeForms")]
    [InlineData("PrintIncludeImage")]
    [InlineData("PrintPreview")]
    [InlineData("PrintPrev")]
    [InlineData("PrintNext")]
    [InlineData("PrintSummary")]
    public void EveryControlOfTheDesignsAccessibilityTableHasItsAutomationId(string id) =>
        Assert.Contains($"AutomationProperties.AutomationId=\"{id}\"", SheetXaml, StringComparison.Ordinal);

    [Theory]
    [InlineData("PrintProgress")]
    [InlineData("PrintProgressBar")]
    [InlineData("PrintStatus")]
    [InlineData("PrintCancel")]
    [InlineData("PrintButton")]
    public void TheProgressStripAndThePrintButtonHaveTheirAutomationIds(string id) =>
        Assert.Contains($"AutomationProperties.AutomationId=\"{id}\"", WindowXaml, StringComparison.Ordinal);

    [Fact]
    public void TheDialogIsNamedPrintAndItsPrimaryButtonIsTheDefault()
    {
        var code = File.ReadAllText(App("MainWindow.Print.cs"));
        Assert.Contains("SetAutomationId(dialog, \"PrintDialog\")", code, StringComparison.Ordinal);
        Assert.Contains("DefaultButton = ContentDialogButton.Primary", code, StringComparison.Ordinal);
        Assert.Contains("Strings.Get(\"Print_Title\")", code, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOptionsComeBeforeThePreviewInTheMarkupBecauseTheTabOrderIsTheDocumentOrder()
    {
        var xaml = SheetXaml;
        var ids = new[] { "PrintPrinter", "PrintPreferences", "PrintCopies", "PrintCollate", "PrintPagesAll", "PrintRange", "PrintSubset", "PrintSides", "PrintOrientation", "PrintAutoRotate", "PrintScale", "PrintPagesPerSheet", "PrintSheetOrder", "PrintPaper", "PrintColour", "PrintIncludeAnnotations", "PrintIncludeForms", "PrintIncludeImage", "PrintPrev", "PrintNext" };
        var positions = ids.Select(id => xaml.IndexOf($"AutomationProperties.AutomationId=\"{id}\"", StringComparison.Ordinal)).ToList();
        Assert.All(positions, p => Assert.True(p >= 0));
        Assert.Equal(positions.Order(), positions);
    }

    [Fact]
    public void TheSummaryIsALivePolitelyTheRangeErrorIsAssertiveAndProgressIsAnnouncedByNotification()
    {
        Assert.Matches(@"x:Name=""SummaryText""[^>]*LiveSetting=""Polite""", SheetXaml);
        Assert.Matches(@"x:Name=""RangeError""[^>]*LiveSetting=""Assertive""", SheetXaml);
        Assert.Contains("x:Name=\"PrintAnnouncement\"", WindowXaml, StringComparison.Ordinal); // progress is announced by UIA notifications from an element that is always there
    }

    [Fact]
    public void ALiveRegionKeepsItsTextAsItsNameSoTheAnnouncementIsTheText()
    {
        // A fixed accessible name on a live region would be announced instead of the text that changed.
        Assert.DoesNotMatch(@"SetName\((SummaryText|RangeError|PagerText|PreviewStatus)", SheetCode);
        Assert.DoesNotMatch(@"SetName\((PrintStatus|PrintDetail)", File.ReadAllText(App("MainWindow.Print.cs")));
    }
}
