using System.Globalization;
using MikePDF.Core.Commands;
using MikePDF.Core.Printing;
using MikePDF.Core.Settings;
using MikePDF.Protocol;

namespace MikePDF.Core.Tests;

/// <summary>
/// The print sheet's rules with no window (docs/features/print.md 3.2, 3.3, 3.7 and 6; design 11-print section 4): the values it opens with, what a printer's abilities do to them, the summary, the
/// failures in words, the settings it keeps, and why Print is off. The sheet itself is driven by the UI journeys (PRINT-13, -21, -22).
/// </summary>
public sealed class PrintSheetTests
{
    private static readonly PrinterAbilities Duplexer = new([new PaperChoice(9, "A4", 2100, 2970), new PaperChoice(1, "Letter", 2159, 2794)], Duplex: true, Colour: true, Collate: true, MaxCopies: 99);
    private static readonly PrinterAbilities PdfQueue = new([new PaperChoice(9, "A4", 2100, 2970)], Duplex: false, Colour: true, Collate: false, MaxCopies: 1);

    private static PrintSheetState Plain(Func<PrintOptions, PrintOptions>? change = null, PrintScope scope = PrintScope.AllPages, string range = "", PageSubset subset = PageSubset.All, PrinterAbilities? abilities = null) =>
        new() { Scope = scope, RangeText = range, Subset = subset, Options = change?.Invoke(new PrintOptions()) ?? new PrintOptions(), Abilities = abilities ?? Duplexer };

    // ---- the settings the sheet keeps ----------------------------------------------------------------------------------------------------

    [Fact]
    public void EveryPrintSettingIsInTheRegistryWithItsWordsAndOnlyThose()
    {
        var registered = SettingsRegistry.All.Where(s => s.Key.StartsWith("print.", StringComparison.Ordinal)).Select(s => s.Key).Order(StringComparer.Ordinal).ToList();
        Assert.Equal([.. PrintSettingKeys.All.Order(StringComparer.Ordinal)], registered);
        foreach (var key in registered)
        {
            var setting = SettingsRegistry.Find(key)!;
            Assert.True(ResourceText.Has(setting.NameKey), setting.NameKey);
            Assert.True(ResourceText.Has(setting.DescriptionKey), setting.DescriptionKey);
            Assert.Equal("print", setting.Owner);
        }
    }

    [Fact]
    public void ThePrinterAndPaperAreDeviceStateAndAreNotExported()
    {
        Assert.False(SettingsRegistry.Find(PrintSettingKeys.Printer)!.Exported);
        Assert.False(SettingsRegistry.Find(PrintSettingKeys.Paper)!.Exported);
        Assert.True(SettingsRegistry.Find(PrintSettingKeys.Scaling)!.Exported);
    }

    [Fact]
    public void LastUsedValuesComeBackThroughTheSettingsFileAndCopiesAndPagesAreNeverKept()
    {
        using var fixture = new SettingsFixture();
        var options = new PrintOptions
        {
            Copies = 7,
            Collate = false,
            Duplex = PrintDuplex.ShortEdge,
            Orientation = PaperOrientation.Landscape,
            PaperSizeId = 9,
            Grayscale = true,
            Scaling = PrintScaling.Custom,
            CustomPercent = 63,
            AutoRotate = false,
            PagesPerSheet = 4,
            SheetOrder = SheetOrder.Vertical,
            Annotations = false,
            FormFields = false,
            AsImage = true,
        };
        using (var store = fixture.Open())
        {
            PrintPreferences.Save(store, "Microsoft Print to PDF", options);
            store.Flush();
        }

        using var again = fixture.Open();
        var loaded = PrintPreferences.Load(again);
        Assert.Equal("Microsoft Print to PDF", loaded.Printer);
        Assert.Equal(options with { Copies = 1 }, loaded.Options);
        var text = File.ReadAllText(fixture.SettingsPath);
        Assert.DoesNotContain(".pdf", text.Replace("Microsoft Print to PDF", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ADamagedValueInTheFileGivesTheDefaultForThatValueOnly()
    {
        using var fixture = new SettingsFixture();
        File.WriteAllText(fixture.SettingsPath, """{ "schemaVersion": 1, "values": { "print.scaling": "Sideways", "print.pagesPerSheet": 3, "print.customPercent": 5000, "print.annotations": false, "print.printer": 12 } }""");
        using var store = fixture.Open();
        var loaded = PrintPreferences.Load(store);
        Assert.Equal(PrintScaling.Shrink, loaded.Options.Scaling);
        Assert.Equal(100, loaded.Options.CustomPercent);
        Assert.False(loaded.Options.Annotations); // the one good value survives
        Assert.Equal(string.Empty, loaded.Printer);
        Assert.Contains(loaded.Options.PagesPerSheet, PrintOptions.PagesPerSheetChoices);
    }

    // ---- the values the sheet opens with --------------------------------------------------------------------------------------------------

    [Fact]
    public void TheSheetOpensWithTheLastUsedValuesAndADocumentWithNoWishesChangesNothing()
    {
        var saved = new PrintPreferences("HP", new PrintOptions { Scaling = PrintScaling.Fit, Duplex = PrintDuplex.LongEdge, Annotations = false });
        var state = PrintSheetModel.Initial(saved, PrintDefaults.Plain);
        Assert.Equal("HP", state.Printer);
        Assert.Equal(PrintScaling.Fit, state.Options.Scaling);
        Assert.Equal(PrintDuplex.LongEdge, state.Options.Duplex);
        Assert.False(state.Options.Annotations);
        Assert.Equal(1, state.Options.Copies);
        Assert.Equal(PrintScope.AllPages, state.Scope);
    }

    [Fact]
    public void AdocumentsWishesWinOverTheLastUsedValuesForWhatTheySay()
    {
        var saved = new PrintPreferences(string.Empty, new PrintOptions { Scaling = PrintScaling.Fit, Duplex = PrintDuplex.None });
        var hints = PrintDefaults.FromViewerPreferences(printScaling: 1, numCopies: 3, duplex: 2, [2u], [4u], pageCount: 10);
        var state = PrintSheetModel.Initial(saved, hints);
        Assert.Equal(PrintScaling.Actual, state.Options.Scaling);
        Assert.Equal(3, state.Options.Copies);
        Assert.Equal(PrintDuplex.ShortEdge, state.Options.Duplex);
        Assert.Equal(PrintScope.Range, state.Scope);
        Assert.Equal("3-5", state.RangeText);
    }

    [Fact]
    public void AHostileDocumentCannotAskForMoreThan99CopiesAJunkRangeOrAScaleOfItsOwn()
    {
        var hints = PrintDefaults.FromViewerPreferences(printScaling: 7, numCopies: int.MaxValue, duplex: 99, [4000000000u, 1u], [5u, 0u], pageCount: 10);
        var state = PrintSheetModel.Initial(PrintPreferences.Default, hints);
        Assert.Equal(99, state.Options.Copies);
        Assert.Equal(PrintScope.AllPages, state.Scope);
        Assert.Equal(PrintScaling.Shrink, state.Options.Scaling);
        Assert.Equal(PrintDuplex.None, state.Options.Duplex);
    }

    [Fact]
    public void PrintCurrentPageStartsOnTheCurrentPageWhateverTheDocumentAsked()
    {
        var hints = new PrintDefaults(PrintScaling.Shrink, 1, PrintDuplex.None, "2-4");
        Assert.Equal(PrintScope.CurrentPage, PrintSheetModel.Initial(PrintPreferences.Default, hints, PrintScope.CurrentPage).Scope);
    }

    [Fact]
    public void ThePrinterIsTheLastUsedOneThenTheDefaultThenPrintToPdfThenTheFirst()
    {
        string[] names = ["Brother", "HP", "Microsoft Print to PDF"];
        Assert.Equal("HP", PrintSheetModel.ChoosePrinter("hp", names, "Brother"));
        Assert.Equal("Brother", PrintSheetModel.ChoosePrinter("Gone", names, "Brother"));
        Assert.Equal("Microsoft Print to PDF", PrintSheetModel.ChoosePrinter("Gone", names, "Also gone"));
        Assert.Equal("Brother", PrintSheetModel.ChoosePrinter(string.Empty, ["Brother", "HP"], null));
        Assert.Null(PrintSheetModel.ChoosePrinter("HP", [], "HP"));
    }

    [Fact]
    public void APrintersAbilitiesDropWhatItCannotDo()
    {
        var wanted = Plain(o => o with { Duplex = PrintDuplex.LongEdge, PaperSizeId = 1 });
        var onDuplexer = PrintSheetModel.WithAbilities(wanted, Duplexer);
        Assert.Equal(PrintDuplex.LongEdge, onDuplexer.Options.Duplex);
        Assert.Equal(1, onDuplexer.Options.PaperSizeId);

        var onPdf = PrintSheetModel.WithAbilities(wanted, PdfQueue);
        Assert.Equal(PrintDuplex.None, onPdf.Options.Duplex);
        Assert.Equal(0, onPdf.Options.PaperSizeId); // Letter is not listed: the printer's own default paper

        var blackAndWhite = PrintSheetModel.WithAbilities(Plain(), new PrinterAbilities([], false, Colour: false, false, 1));
        Assert.True(blackAndWhite.Options.Grayscale);
    }

    [Fact]
    public void WhatIsKnownAboutNoPrinterDoesNotRemoveAnyChoice()
    {
        var state = PrintSheetModel.WithAbilities(Plain(o => o with { PaperSizeId = 9 }), PrinterAbilities.Unknown);
        Assert.Equal(9, state.Options.PaperSizeId); // no paper list: nothing to check it against
        Assert.False(state.Options.Grayscale);
    }

    // ---- what the choices mean ---------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(PrintScope.AllPages, "", PageSubset.All, 1, false, 40, 40, "Print_Summary_PN_SN")]
    [InlineData(PrintScope.Range, "3-7", PageSubset.All, 1, false, 5, 5, "Print_Summary_PN_SN")]
    [InlineData(PrintScope.Range, "3-7", PageSubset.All, 1, true, 5, 3, "Print_Summary_PN_SN_Both")]
    [InlineData(PrintScope.CurrentPage, "", PageSubset.All, 1, false, 1, 1, "Print_Summary_P1_S1")]
    [InlineData(PrintScope.CurrentPage, "", PageSubset.All, 1, true, 1, 1, "Print_Summary_P1_S1_Both")]
    [InlineData(PrintScope.AllPages, "", PageSubset.Odd, 1, false, 20, 20, "Print_Summary_PN_SN")]
    [InlineData(PrintScope.AllPages, "", PageSubset.All, 4, false, 40, 10, "Print_Summary_PN_SN")]
    [InlineData(PrintScope.Range, "1-5", PageSubset.All, 4, false, 5, 2, "Print_Summary_PN_SN")]
    [InlineData(PrintScope.Range, "1-2", PageSubset.All, 4, false, 2, 1, "Print_Summary_PN_S1")]
    [InlineData(PrintScope.AllPages, "", PageSubset.All, 16, true, 40, 2, "Print_Summary_PN_SN_Both")]
    public void TheSummaryCountsPagesAndSheetsTheWayTheJobWillPrintThem(PrintScope scope, string range, PageSubset subset, int perSheet, bool duplex, int pages, int sheets, string key)
    {
        var state = Plain(o => o with { PagesPerSheet = perSheet, Duplex = duplex ? PrintDuplex.LongEdge : PrintDuplex.None }, scope, range, subset);
        var result = PrintSheetModel.Evaluate(state, pageCount: 40, currentPage: 12);
        Assert.True(result.HasPages);
        Assert.Equal(pages, result.PageCount);
        Assert.Equal(sheets, result.Sheets);
        Assert.Equal(key, result.SummaryKey);
        Assert.Equal(duplex, result.BothSides);
        Assert.True(ResourceText.Has(result.SummaryKey!), result.SummaryKey);
    }

    [Fact]
    public void ThePrintedSentenceIsTheOneTheDesignGives()
    {
        var result = PrintSheetModel.Evaluate(Plain(o => o with { Duplex = PrintDuplex.LongEdge }, PrintScope.Range, "3-7"), 40, 1);
        Assert.Equal("Printing 5 pages on 3 sheets, both sides", string.Format(CultureInfo.InvariantCulture, ResourceText.Get(result.SummaryKey!), result.PageCount, result.Sheets));
    }

    [Fact]
    public void DuplexOnAPrinterThatCannotDoItIsNotInTheSummary()
    {
        var result = PrintSheetModel.Evaluate(Plain(o => o with { Duplex = PrintDuplex.LongEdge }, abilities: PdfQueue), 4, 1);
        Assert.False(result.BothSides);
        Assert.Equal(4, result.Sheets);
    }

    [Fact]
    public void CopiesGetALineOfTheirOwn()
    {
        Assert.Null(PrintSheetModel.Evaluate(Plain(), 4, 1).CopiesKey);
        Assert.Equal("Print_Summary_Copies_Collated", PrintSheetModel.Evaluate(Plain(o => o with { Copies = 3 }), 4, 1).CopiesKey);
        Assert.Equal("Print_Summary_Copies_Uncollated", PrintSheetModel.Evaluate(Plain(o => o with { Copies = 3, Collate = false }), 4, 1).CopiesKey);
    }

    [Theory]
    [InlineData("3-1", "Print_Range_Error_Order")]
    [InlineData("a", "Print_Range_Error_Syntax")]
    [InlineData("1,,2", "Print_Range_Error_Syntax")]
    [InlineData("0", "Print_Range_Error_Syntax")]
    [InlineData("41", "Print_Range_Error_Limit")]
    [InlineData("5,1", "Print_Range_Error_Order")]
    public void ABadRangeGivesTheErrorTextAndNoPagesAndPrintStaysOff(string range, string key)
    {
        var result = PrintSheetModel.Evaluate(Plain(scope: PrintScope.Range, range: range), 40, 1);
        Assert.False(result.HasPages);
        Assert.Equal(key, result.RangeErrorKey);
        Assert.Equal(40, result.RangeErrorLimit);
        Assert.True(result.ErrorIsLive);
        Assert.True(ResourceText.Has(key));
        Assert.Null(result.SummaryKey);
    }

    [Fact]
    public void AnEmptyRangeBoxIsNotAnnouncedAsAnErrorButStillStopsPrinting()
    {
        var result = PrintSheetModel.Evaluate(Plain(scope: PrintScope.Range, range: ""), 40, 1);
        Assert.False(result.HasPages);
        Assert.False(result.ErrorIsLive);
    }

    [Fact]
    public void TheLimitIsNamedInTheSentence()
    {
        var result = PrintSheetModel.Evaluate(Plain(scope: PrintScope.Range, range: "99"), 40, 1);
        Assert.Equal("The document has 40 pages.", string.Format(CultureInfo.InvariantCulture, ResourceText.Get(result.RangeErrorKey!), result.RangeErrorLimit));
    }

    [Fact]
    public void AnOddSubsetOfASinglePageEvenPageLeavesNothingToPrint()
    {
        var result = PrintSheetModel.Evaluate(Plain(scope: PrintScope.Range, range: "2", subset: PageSubset.Odd), 40, 1);
        Assert.False(result.HasPages);
        Assert.Equal("Print_Range_Error_None", result.RangeErrorKey);
    }

    [Theory]
    [InlineData(1, 5, 0, "1")]
    [InlineData(1, 5, 4, "5")]
    [InlineData(4, 10, 0, "1,2,3,4")]
    [InlineData(4, 10, 2, "9,10")]
    [InlineData(4, 10, 3, "")]
    [InlineData(4, 10, -1, "")]
    public void ThePagerStepsThroughTheSheetsOfTheSelection(int perSheet, int pageCount, int sheet, string expected)
    {
        var pages = PageSelection.Resolve(PrintScope.AllPages, pageCount, 1, null, null, PageSubset.All).Pages;
        Assert.Equal(expected, string.Join(',', PrintSheetModel.PagesOnSheet(pages, perSheet, sheet)));
    }

    [Fact]
    public void ThePagerPicksPagesOfASetWithGapsInOrder()
    {
        var pages = PageSelection.Resolve(PrintScope.Range, 40, 1, "2-3, 7, 10-11", null, PageSubset.All).Pages;
        Assert.Equal([2, 3], PrintSheetModel.PagesOnSheet(pages, 2, 0));
        Assert.Equal([7, 10], PrintSheetModel.PagesOnSheet(pages, 2, 1));
        Assert.Equal([11], PrintSheetModel.PagesOnSheet(pages, 2, 2));
        Assert.Equal(2, PrintSheetModel.ClampSheet(9, 3));
        Assert.Equal(0, PrintSheetModel.ClampSheet(-4, 3));
        Assert.Equal(0, PrintSheetModel.ClampSheet(4, 0));
    }

    // ---- failures in plain words ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(5, Status.Ok, PrintFailureKind.AccessDenied)]
    [InlineData(1801, Status.Ok, PrintFailureKind.PrinterOffline)]
    [InlineData(1905, Status.Ok, PrintFailureKind.PrinterOffline)]
    [InlineData(21, Status.Ok, PrintFailureKind.PrinterOffline)]
    [InlineData(1722, Status.Ok, PrintFailureKind.SpoolerUnavailable)]
    [InlineData(112, Status.Ok, PrintFailureKind.OutputUnwritable)]
    [InlineData(1223, Status.Ok, PrintFailureKind.UserDeclined)]
    [InlineData(0, Status.EngineCrashed, PrintFailureKind.EngineFailed)]
    [InlineData(0, Status.Timeout, PrintFailureKind.PageTooSlow)]
    [InlineData(31337, Status.Ok, PrintFailureKind.Unknown)]
    [InlineData(0, Status.Ok, PrintFailureKind.Unknown)]
    public void AFailureIsSortedIntoTheFewKindsAPersonCanActOn(int win32, Status status, PrintFailureKind expected) => Assert.Equal(expected, PrintFailure.Classify(win32, status));

    [Fact]
    public void EveryKindOfFailureHasASentenceThatNamesNoErrorCodes()
    {
        foreach (var kind in Enum.GetValues<PrintFailureKind>())
        {
            var key = PrintFailure.ResourceKey(kind);
            Assert.True(ResourceText.Has(key), key);
            var text = ResourceText.Get(key);
            Assert.DoesNotMatch(@"0x[0-9A-Fa-f]+|error \d+|Win32|HRESULT|exception", text);
        }
    }

    [Fact]
    public void TheOfflineSentenceIsTheOneTheDesignGives() =>
        Assert.Equal("The printer didn't accept the job. Check that it is on and connected, then try again.", ResourceText.Get(PrintFailure.ResourceKey(PrintFailureKind.PrinterOffline)));

    // ---- why Print is off (PRINT-22) -------------------------------------------------------------------------------------------------------

    private static CommandAvailability Print(CommandContext context) => CommandCatalog.ById["file.print"].CanExecute(context);

    [Fact]
    public void PrintIsOnForAnOpenDocumentAndTheReasonForEachWayItIsOffIsInWords()
    {
        var open = new CommandContext { HasDocument = true, PageCount = 5, CurrentPage = 1 };
        Assert.True(Print(open).IsEnabled);
        Assert.Equal(Reasons.NoDocument, Print(CommandContext.Empty).Reason);
        Assert.Equal(Reasons.Policy, Print(open with { PrintingAllowed = false }).Reason);
        Assert.Equal(Reasons.OwnerNoPrint, Print(open with { OwnerAllowsPrinting = false }).Reason);
        Assert.Equal(Reasons.PrintBusy, Print(open with { PrintJobRunning = true }).Reason);
        foreach (var reason in new[] { Reasons.NoDocument, Reasons.Policy, Reasons.OwnerNoPrint, Reasons.PrintBusy, Reasons.NotPrinting })
        {
            Assert.True(ResourceText.Has(Reasons.ResourceKey(reason)), reason);
        }
    }

    [Fact]
    public void AnOrganisationsPolicyIsTheReasonShownEvenWhenTheOwnerAlsoForbidsPrinting()
    {
        var context = new CommandContext { HasDocument = true, PrintingAllowed = false, OwnerAllowsPrinting = false };
        Assert.Equal(Reasons.Policy, Print(context).Reason);
    }

    [Fact]
    public void TheOwnersReasonIsTheSentenceOfTheSpec()
    {
        Assert.Equal("The document's owner doesn't allow printing.", ResourceText.Get(Reasons.ResourceKey(Reasons.OwnerNoPrint)));
        Assert.Equal(ResourceText.Get(PrintStrings.DisabledOwner), ResourceText.Get(Reasons.ResourceKey(Reasons.OwnerNoPrint)));
    }

    [Fact]
    public void ThePaletteRowForPrintCarriesTheOwnersReasonAndTheShortcut()
    {
        var registry = new CommandRegistry();
        var context = new CommandContext { HasDocument = true, OwnerAllowsPrinting = false };
        var source = new CommandPaletteSource(registry, () => context, ResourceText.Get);
        var row = source.Items(string.Empty).Single(i => i.Key == "file.print");
        Assert.False(row.Enabled);
        Assert.Equal("The document's owner doesn't allow printing.", row.DisabledReason);
        Assert.Equal("Ctrl+P", row.Shortcut);
    }

    [Fact]
    public void PrintCurrentPageAndCancelPrintingFollowTheirOwnRules()
    {
        var open = new CommandContext { HasDocument = true };
        Assert.True(CommandCatalog.ById["file.printCurrentPage"].CanExecute(open).IsEnabled);
        Assert.False(CommandCatalog.ById["file.printCurrentPage"].CanExecute(open with { OwnerAllowsPrinting = false }).IsEnabled);
        Assert.Equal(Reasons.NotPrinting, CommandCatalog.ById["print.cancel"].CanExecute(open).Reason);
        Assert.True(CommandCatalog.ById["print.cancel"].CanExecute(open with { PrintJobRunning = true }).IsEnabled);
        Assert.True(CommandCatalog.ById["print.cancel"].CanExecute(open with { PrintJobRunning = true, Presentation = true }).IsEnabled);
    }

    [Fact]
    public void CtrlPIsPrintAndNothingElseUsesIt() =>
        Assert.Equal(["file.print"], CommandCatalog.All.Where(c => c.DefaultChords.Any(x => x.ToString() == "Ctrl+P")).Select(c => c.Id));
}
