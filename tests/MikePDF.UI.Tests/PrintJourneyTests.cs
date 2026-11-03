using System.Diagnostics;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.WindowsAPI;
using Microsoft.Win32;
using MikePDF.Core.Printing;
using MikePDF.UI.Tests.Pages;

namespace MikePDF.UI.Tests;

/// <summary>
/// Printing as a person does it (docs/features/print.md, docs/design/screens/11-print.md): Ctrl+P, the sheet, the options, the strip, and the file Microsoft Print to PDF wrote. Journey 11 (master prompt
/// 12.5) and PRINT-11, -13, -21, -22. The app writes the job to a folder the journey names (MIKEPDF_TEST_PRINT_OUTPUT), so the queue's Save dialog never appears; the engine of the test checks the file
/// (page count, page sizes) and compares each page with the page it came from (SSIM at the printer's resolution, box filtered, as the pipeline tests do). Each journey starts its own MikePDF.
/// </summary>
public sealed class PrintJourneyTests : IDisposable
{
    private const string Queue = PrintSheetPage.PdfQueue;
    private const double SsimFloor = 0.98; // print.md PRINT-13: the target is 0.99 at readable sizes; 0.98 is accepted for shrunk text (decisions.md)

    private readonly string _work = Path.Combine(Path.GetTempPath(), "mikepdf-ui-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _out;
    private EngineLab? _lab;

    public PrintJourneyTests()
    {
        MikePDFApp.KillAll();
        AppData.ResetSettings(); // each journey starts from the defaults, whatever an earlier one left
        _out = Path.Combine(_work, "printed");
        Directory.CreateDirectory(_out);
    }

    private EngineLab Lab => _lab ??= new EngineLab();

    public void Dispose()
    {
        MikePDFApp.KillAll();
        AppData.ResetSettings();
        Environment.SetEnvironmentVariable("MIKEPDF_TEST_POLICY_KEY", null);
        _lab?.Dispose();
        try
        {
            if (Directory.Exists(_work))
            {
                Directory.Delete(_work, recursive: true);
            }
        }
        catch (IOException)
        {
            // Still held for a moment by the process that just ended.
        }
    }

    private MikePDFApp Launch(string pdf, bool rtl = false)
    {
        var environment = new Dictionary<string, string> { ["MIKEPDF_TEST_PRINT_OUTPUT"] = _out };
        if (rtl)
        {
            environment["MIKEPDF_TEST_PSEUDO_RTL"] = "1";
        }

        var app = MikePDFApp.Launch(pdf, environment: environment);
        return app;
    }

    private static void Note(string line)
    {
        try
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "MikePDF.slnx")))
                {
                    var path = Path.Combine(directory.FullName, "build", "out", "print-ui-evidence.txt");
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.AppendAllText(path, $"{DateTimeOffset.Now:u} {line}{Environment.NewLine}");
                    return;
                }
            }
        }
        catch (IOException)
        {
            // Evidence is a convenience.
        }
    }

    /// <summary>Opens the sheet on a document that is open, picks the Print To PDF queue on A4 portrait paper, and waits until it is ready.</summary>
    private static PrintSheetPage OpenOnPdfQueue(MikePDFApp app, string scale = "Fit to printable area")
    {
        app.FocusPages();
        var sheet = new PrintSheetPage(app);
        sheet.Open();
        sheet.WaitUntilReady();
        sheet.ChoosePrinter(Queue);
        sheet.Choose("PrintPaper", "A4");
        sheet.Choose("PrintOrientation", "Portrait");
        sheet.Choose("PrintScale", scale);
        return sheet;
    }

    private string Printed(int job) => Path.Combine(_out, $"print-{job}.pdf");

    // ---- journey 11, PRINT-13 ---------------------------------------------------------------------------------------------------------------------------

    [UiFact]
    public async Task J11_APageRangePrintedToPdfIsTheRightPagesAtTheRightSizeAndMatchesTheSource()
    {
        var pdf = TestPdfs.Numbered(_work, "report.pdf", 8);
        using var app = Launch(pdf);
        app.WaitForDocument(8);
        var sheet = OpenOnPdfQueue(app);

        // The options are on one side and the preview on the other (design 11-print section 3.1).
        sheet.WaitForPreview();
        Assert.True(app.Require("PrintPreview").BoundingRectangle.Left < app.Require("PrintPrinter").BoundingRectangle.Left, "the preview is left of the printer drop-down, which is the first option");

        sheet.SetRange("3-5");
        app.WaitFor("the summary", () => sheet.Summary == "Printing 3 pages on 3 sheets");
        app.WaitFor("the preview of page 3", () => sheet.PreviewName == "Preview of page 3. Portrait, A4, scaled to fit.", TimeSpan.FromSeconds(60));
        app.Capture("print-sheet");
        sheet.Print();
        app.WaitFor("the strip", () => sheet.StripShown);
        sheet.WaitForDone();
        Assert.False(app.Exists("PrintDialog"));

        await using var printed = await Lab.OpenAsync(Printed(1));
        await using var source = await Lab.OpenAsync(pdf);
        Assert.Equal(3, printed.PageCount);
        Assert.All(printed.Sizes, s =>
        {
            Assert.InRange(s.Width, 595.28 - PdfFile.SizeTolerance, 595.28 + PdfFile.SizeTolerance);
            Assert.InRange(s.Height, 841.89 - PdfFile.SizeTolerance, 841.89 + PdfFile.SizeTolerance);
        });
        var paper = PdfFile.PdfQueueA4();
        var scores = new List<string>();
        for (var page = 0; page < 3; page++)
        {
            var ssim = await printed.SsimAsync(page, source, 2 + page, paper, PrintScaling.Fit, 100, autoRotate: true);
            scores.Add($"p{3 + page}={ssim:F4}");
            Assert.True(ssim >= SsimFloor, $"printed page {page + 1} (document page {3 + page}) against the source: SSIM {ssim:F4}, wanted {SsimFloor}");
        }

        Note("J11 range 3-5 fit: SSIM " + string.Join(' ', scores));

        // The page that was not asked for is not in the file: page 2 of the file is the document's page 4.
        var wrong = await printed.SsimAsync(0, source, 5, paper, PrintScaling.Fit, 100, autoRotate: true);
        Assert.True(wrong < 0.9990, $"the first printed page is not document page 6 (SSIM {wrong:F4}, against 0.9998 for page 3)");
    }

    [UiFact]
    public async Task J11_FourPagesOnASheetPrintHalfAsManySheetsAndEachCellMatchesItsPage()
    {
        var pdf = TestPdfs.Numbered(_work, "eight.pdf", 8);
        using var app = Launch(pdf);
        app.WaitForDocument(8);
        var sheet = OpenOnPdfQueue(app);
        sheet.Choose("PrintPagesPerSheet", "4 pages");
        app.WaitFor("the summary", () => sheet.Summary == "Printing 8 pages on 2 sheets");
        app.WaitFor("the preview of sheet 1", () => sheet.PreviewName.StartsWith("Preview of sheet 1, with 4 pages on it.", StringComparison.Ordinal), TimeSpan.FromSeconds(60));
        Assert.False(sheet.IsEnabled("PrintScale"), "N-up fits every page in its place: the scale does not apply");
        Assert.True(sheet.IsEnabled("PrintSheetOrder"));
        sheet.Print();
        sheet.WaitForDone();

        await using var printed = await Lab.OpenAsync(Printed(1));
        await using var source = await Lab.OpenAsync(pdf);
        Assert.Equal(2, printed.PageCount);
        var paper = PdfFile.PdfQueueA4();
        var cells = SheetLayout.Cells(paper, 4, SheetOrder.Horizontal);
        var scores = new List<string>();
        for (var sheetIndex = 0; sheetIndex < 2; sheetIndex++)
        {
            for (var i = 0; i < 4; i++)
            {
                var (x, y, area) = cells[i];
                var ssim = await printed.SsimAsync(sheetIndex, source, (sheetIndex * 4) + i, paper, PrintScaling.Fit, 100, autoRotate: false, x, y, area);
                scores.Add($"s{sheetIndex + 1}c{i + 1}={ssim:F4}");
                Assert.True(ssim >= SsimFloor, $"sheet {sheetIndex + 1}, cell {i + 1} (document page {(sheetIndex * 4) + i + 1}): SSIM {ssim:F4}");
            }
        }

        Note("J11 4-up: SSIM " + string.Join(' ', scores));
    }

    [UiFact]
    public async Task J11_ActualSizeAndCustomScalePrintAtTheScaleAsked()
    {
        var pdf = TestPdfs.Numbered(_work, "two.pdf", 2);
        using var app = Launch(pdf);
        app.WaitForDocument(2);
        var sheet = OpenOnPdfQueue(app, "Custom scale");
        app.Require("PrintScaleCustom").Patterns.RangeValue.Pattern.SetValue(50);
        app.WaitFor("the preview at 50 percent", () => sheet.PreviewName == "Preview of page 1. Portrait, A4, scaled to 50 percent.", TimeSpan.FromSeconds(60));
        sheet.Print();
        sheet.WaitForDone();

        await using var printed = await Lab.OpenAsync(Printed(1));
        await using var source = await Lab.OpenAsync(pdf);
        Assert.Equal(2, printed.PageCount);
        var paper = PdfFile.PdfQueueA4();
        for (var page = 0; page < 2; page++)
        {
            var ssim = await printed.SsimAsync(page, source, page, paper, PrintScaling.Custom, 50, autoRotate: true);
            Note($"J11 custom 50%: page {page + 1} SSIM {ssim:F4}");
            Assert.True(ssim >= SsimFloor, $"page {page + 1} at 50 percent: SSIM {ssim:F4}");
        }
    }

    // ---- PRINT-11 ---------------------------------------------------------------------------------------------------------------------------------------------

    [UiFact]
    public async Task PRINT11_CancelStopsTheJobAtOnceLeavesNoFileOrSpoolJobAndTheNextPrintWorks()
    {
        var pdf = TestPdfs.Numbered(_work, "long.pdf", 60);
        using var app = Launch(pdf);
        app.WaitForDocument(60);
        var sheet = OpenOnPdfQueue(app);
        sheet.Print();
        app.WaitFor("the third page counted, so two are done", () => sheet.StripStatus.StartsWith("Printing page 3 of", StringComparison.Ordinal), TimeSpan.FromSeconds(120));
        Assert.True(app.Require("PrintCancel").IsEnabled);

        var clock = Stopwatch.StartNew();
        sheet.CancelJob();
        app.WaitFor("the strip to say where it stopped", () => sheet.StripStatus.StartsWith("Stopped after page", StringComparison.Ordinal), TimeSpan.FromSeconds(10));
        var took = clock.Elapsed;
        Note($"PRINT-11 (UI): {took.TotalMilliseconds:F0} ms from pressing Cancel to the strip saying '{sheet.StripStatus}'");
        Assert.True(took < TimeSpan.FromSeconds(2.5), $"cancel took {took.TotalMilliseconds:F0} ms (the engine is idle within 1 s; this is the whole round trip through the UI)");

        Assert.True(!File.Exists(Printed(1)) || new FileInfo(Printed(1)).Length == 0, "no partial output");
        var deadline = Stopwatch.StartNew();
        while (PrintQueue.JobsIn(Queue) > 0 && deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(200);
        }

        Assert.Equal(0, PrintQueue.JobsIn(Queue));
        var status = sheet.StripStatus;
        await Task.Delay(1500);
        Assert.Equal(status, sheet.StripStatus); // nothing more is printed or counted

        // The next print works.
        app.FocusPages();
        sheet.Open();
        sheet.WaitUntilReady();
        sheet.SetRange("1");
        sheet.Print();
        sheet.WaitForDone();
        await using var printed = await Lab.OpenAsync(Printed(2));
        Assert.Equal(1, printed.PageCount);
    }

    // ---- PRINT-22 -----------------------------------------------------------------------------------------------------------------------------------------------

    private const string OwnerReason = "The document's owner doesn't allow printing.";

    [UiFact]
    public void PRINT22_WhenTheOwnerForbidsPrintingTheReasonIsInHelpTextThePaletteTheBannerAndTheTooltip()
    {
        using (var app = MikePDFApp.Launch(MikePDFApp.Fixture("encrypted.pdf")))
        {
            app.Require("PasswordDialog");
            app.Type("1234"); // the user password: the file's restrictions apply
            app.Press(VirtualKeyShort.ENTER);
            app.WaitForDocument(1);
            app.FocusPages();

            var button = app.Require("PrintButton");
            Assert.False(button.IsEnabled);
            Assert.Equal(OwnerReason, app.HelpText("PrintButton"));

            // The palette lists Print, unavailable, with the reason.
            app.Palette.Open();
            app.Palette.Type("print");
            app.Palette.WaitForRow("Print, Ctrl+P");
            Assert.Contains(app.Palette.Rows(), r => r.StartsWith("Print, Ctrl+P", StringComparison.Ordinal) && r.Contains("unavailable", StringComparison.Ordinal) && r.Contains(OwnerReason, StringComparison.Ordinal));
            app.Palette.Close();

            // Ctrl+P says why instead of opening the sheet.
            app.FocusPages();
            app.PressWithControl(VirtualKeyShort.KEY_P);
            app.WaitFor("the banner that says why", () => app.Window.FindFirstDescendant(cf => cf.ByName(OwnerReason)) is not null || app.Window.FindFirstDescendant(cf => cf.ByName("Print is not available")) is not null, TimeSpan.FromSeconds(10));
            Assert.False(app.Exists("PrintPrinter"));

            // And the tooltip of the button.
            var tip = HoverTooltip(app, button, "(Ctrl+P)");
            Assert.Contains(OwnerReason, tip, StringComparison.Ordinal);
        }

        using var owner = MikePDFApp.Launch(MikePDFApp.Fixture("encrypted.pdf"));
        owner.Require("PasswordDialog");
        owner.Type("5678"); // the owner password: nothing is forbidden
        owner.Press(VirtualKeyShort.ENTER);
        owner.WaitForDocument(1);
        owner.WaitFor("Print on for the owner", () => owner.Require("PrintButton").IsEnabled);
        Assert.Equal(string.Empty, owner.HelpText("PrintButton"));
    }

    [UiFact]
    public void PRINT22_WhenTheOrganisationTurnsPrintingOffItIsOffWithItsOwnReasonAndComesBack()
    {
        var path = @"Software\MikePDF.Tests\print-" + Guid.NewGuid().ToString("N")[..8];
        Environment.SetEnvironmentVariable("MIKEPDF_TEST_POLICY_KEY", path);
        try
        {
            using var app = MikePDFApp.Launch(TestPdfs.Numbered(_work, "policy.pdf", 2));
            app.WaitForDocument(2);
            app.WaitFor("Print on", () => app.Require("PrintButton").IsEnabled);

            using (var key = Registry.CurrentUser.CreateSubKey(path))
            {
                key.SetValue("PrintingEnabled", 0, RegistryValueKind.DWord);
            }

            app.WaitFor("Print off", () => !app.Require("PrintButton").IsEnabled, TimeSpan.FromSeconds(10));
            Assert.Equal("Turned off by your organisation", app.HelpText("PrintButton"));
            app.Palette.Open();
            app.Palette.Type("print");
            app.Palette.WaitForRow("Print, Ctrl+P");
            Assert.Contains(app.Palette.Rows(), r => r.StartsWith("Print, Ctrl+P", StringComparison.Ordinal) && r.Contains("Turned off by your organisation", StringComparison.Ordinal));
            app.Palette.Close();

            using (var key = Registry.CurrentUser.CreateSubKey(path))
            {
                key.SetValue("PrintingEnabled", 1, RegistryValueKind.DWord);
            }

            app.WaitFor("Print on again", () => app.Require("PrintButton").IsEnabled, TimeSpan.FromSeconds(10));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
            using var parent = Registry.CurrentUser.OpenSubKey(@"Software\MikePDF.Tests");
            if (parent is { SubKeyCount: 0 })
            {
                parent.Dispose();
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\MikePDF.Tests", throwOnMissingSubKey: false);
            }
        }
    }

    /// <summary>Hovers a control the way a mouse does and returns the text of the tooltip that shows (empty if none comes).</summary>
    private static string HoverTooltip(MikePDFApp app, AutomationElement element, string marker)
    {
        var centre = MikePDFApp.CenterOf(element.BoundingRectangle);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            NativeMouse.MoveTo(new System.Drawing.Point(centre.X - 30, centre.Y + 40));
            Thread.Sleep(150);
            NativeMouse.MoveTo(new System.Drawing.Point(centre.X - 6, centre.Y + 4));
            Thread.Sleep(150);
            NativeMouse.MoveTo(centre);
            Thread.Sleep(1800);
            if (attempt == 0)
            {
                var shots = Environment.GetEnvironmentVariable("MIKEPDF_SHOTS_DIR") ?? Path.Combine(AppContext.BaseDirectory, "shots");
                Directory.CreateDirectory(shots);
                using var picture = FlaUI.Core.Capturing.Capture.Rectangle(new System.Drawing.Rectangle(Math.Max(0, centre.X - 300), Math.Max(0, centre.Y - 100), 700, 300));
                picture.ToFile(Path.Combine(shots, "hover-disabled-button.png"));
            }

            try
            {
                // A tooltip of WinUI (an enabled control), or the flyout that stands in for one on a control that is off (WinUI shows no tooltip for those): its words are a text with the marker in it.
                var tip = app.Window.FindFirstDescendant(cf => cf.ByControlType(ControlType.ToolTip)) ?? app.Automation.GetDesktop().FindFirstDescendant(cf => cf.ByControlType(ControlType.ToolTip));
                if (tip is not null)
                {
                    var text = tip.Properties.Name.ValueOrDefault;
                    text = string.IsNullOrEmpty(text) ? string.Join(' ', tip.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)).Select(t => t.Properties.Name.ValueOrDefault)) : text;
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text;
                    }
                }

                // (a popup of this app can be a window of its own: every top-level window of the process is looked at)
                foreach (var top in app.Automation.GetDesktop().FindAllChildren().Where(w => w.Properties.ProcessId.ValueOrDefault == app.ProcessId))
                {
                    var words = top.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)).Select(t => t.Properties.Name.ValueOrDefault).FirstOrDefault(n => n is not null && n.Contains(marker, StringComparison.Ordinal));
                    if (words is not null)
                    {
                        return words;
                    }
                }
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // the tooltip went away while it was read: look again
            }
        }

        return string.Empty;
    }

    // ---- the sheet's checks and its memory --------------------------------------------------------------------------------------------------------

    [UiFact]
    public void ARangeThatIsNotValidIsFlaggedInlineKeepsPrintOffAndIsFixedByCorrectingIt()
    {
        using var app = Launch(TestPdfs.Numbered(_work, "forty.pdf", 40));
        app.WaitForDocument(40);
        var sheet = OpenOnPdfQueue(app);
        Assert.Equal("Printing 40 pages on 40 sheets", sheet.Summary);

        sheet.SetRange("3-1");
        app.WaitFor("the order error", () => sheet.RangeError.Contains("Pages must be in order", StringComparison.Ordinal), TimeSpan.FromSeconds(5));
        Assert.StartsWith("That range isn't valid.", sheet.RangeError, StringComparison.Ordinal);
        Assert.False(sheet.PrintButton!.IsEnabled);
        Assert.True(sheet.IsScope("PrintPagesRange"), "typing in the range box chooses the range");

        sheet.SetRange("99");
        app.WaitFor("the limit error", () => sheet.RangeError.Contains("The document has 40 pages.", StringComparison.Ordinal), TimeSpan.FromSeconds(5));
        Assert.False(sheet.PrintButton!.IsEnabled);
        Assert.Equal("The document has 40 pages.", app.HelpText("PrintRange"));

        sheet.SetRange("1,3,5-8");
        app.WaitFor("no error", () => sheet.RangeError.Length == 0, TimeSpan.FromSeconds(5));
        app.WaitFor("the summary", () => sheet.Summary == "Printing 6 pages on 6 sheets");
        Assert.True(sheet.PrintButton!.IsEnabled);

        sheet.Choose("PrintSubset", "Odd pages only");
        app.WaitFor("the odd summary", () => sheet.Summary == "Printing 4 pages on 4 sheets"); // 1, 3, 5, 7
        sheet.Cancel();
    }

    [UiFact]
    public async Task TheLastUsedOptionsComeBackWhenTheSheetOpensAgainAndTheSettingsFileHoldsNoPaths()
    {
        var pdf = TestPdfs.Numbered(_work, "memory.pdf", 2);
        using (var first = Launch(pdf))
        {
            first.WaitForDocument(2);
            var sheet = OpenOnPdfQueue(first, "Actual size");
            sheet.Check("PrintAutoRotate", false); // not with several pages per sheet (their cells fit every page), so before choosing them
            sheet.Choose("PrintPagesPerSheet", "2 pages");
            sheet.Choose("PrintOrientation", "Landscape");
            sheet.Choose("PrintSheetOrder", "Down, then across");
            sheet.Check("PrintIncludeAnnotations", false);
            sheet.Check("PrintIncludeImage", true);
            sheet.Choose("PrintColour", "Black and white");
            sheet.Print();
            sheet.WaitForDone();
            first.CloseGracefully();
        }

        using var again = Launch(pdf);
        again.WaitForDocument(2);
        again.FocusPages();
        var reopened = new PrintSheetPage(again);
        reopened.Open();
        reopened.WaitUntilReady();
        Assert.Equal(Queue, reopened.Printer);
        again.WaitFor("the remembered paper", () => reopened.Selected("PrintPaper") == "A4"); // the printer's paper list comes a moment after the sheet
        Assert.Equal("Actual size", reopened.Selected("PrintScale"));
        Assert.Equal("2 pages", reopened.Selected("PrintPagesPerSheet"));
        Assert.Equal("Landscape", reopened.Selected("PrintOrientation"));
        Assert.Equal("Down, then across", reopened.Selected("PrintSheetOrder"));
        Assert.Equal("Black and white", reopened.Selected("PrintColour"));
        Assert.False(reopened.IsChecked("PrintIncludeAnnotations"));
        Assert.True(reopened.IsChecked("PrintIncludeImage"));
        Assert.False(reopened.IsChecked("PrintAutoRotate"));
        Assert.True(reopened.IsChecked("PrintIncludeForms"));
        Assert.True(reopened.IsScope("PrintPagesAll")); // pages belong to a document, not to the sheet
        reopened.Cancel();

        again.CloseGracefully();
        var settings = Path.Combine(AppData.PrivateFolder()!, "settings.json");
        var text = await File.ReadAllTextAsync(settings);
        Assert.Matches(@"""print\.scaling""\s*:\s*""Actual""", text);
        Assert.Matches(@"""print\.pagesPerSheet""\s*:\s*2\b", text);
        Assert.DoesNotContain(".pdf", text.Replace(Queue, string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_work, text, StringComparison.OrdinalIgnoreCase);
    }

    // ---- right to left -------------------------------------------------------------------------------------------------------------------------------------

    [UiFact]
    public async Task RTL_ThePrintSheetMirrorsAndTheDocumentsPageInThePreviewAndOnPaperDoesNot()
    {
        var pdf = TestPdfs.Numbered(_work, "rtl.pdf", 3);
        using var app = Launch(pdf, rtl: true);
        app.WaitForDocument(3);
        var sheet = OpenOnPdfQueue(app);
        sheet.WaitForPreview(); // the picture has its place (an empty image sits wherever the layout left it)
        Assert.True(app.Require("PrintPreview").BoundingRectangle.Left > app.Require("PrintPrinter").BoundingRectangle.Left, "mirrored: the options are on the left and the preview on the right");
        sheet.SetRange("2");
        app.WaitFor("the preview of page 2", () => sheet.PreviewName == "Preview of page 2. Portrait, A4, scaled to fit.", TimeSpan.FromSeconds(60));
        sheet.Print();
        sheet.WaitForDone();

        await using var printed = await Lab.OpenAsync(Printed(1));
        await using var source = await Lab.OpenAsync(pdf);
        Assert.Equal(1, printed.PageCount);
        var ssim = await printed.SsimAsync(0, source, 1, PdfFile.PdfQueueA4(), PrintScaling.Fit, 100, autoRotate: true);
        Note($"RTL: printed page against the source SSIM {ssim:F4}");
        Assert.True(ssim >= SsimFloor, $"a page is not mirrored with the window: SSIM {ssim:F4}");
    }

    // ---- accessibility: PRINT-21 --------------------------------------------------------------------------------------------------------------------------

    [UiFact]
    public void PRINT21_EveryControlOfTheSheetHasANameARoleAndTheTabOrderIsTheVisualOrder()
    {
        using var app = Launch(TestPdfs.Numbered(_work, "names.pdf", 8));
        app.WaitForDocument(8);
        var sheet = OpenOnPdfQueue(app);
        sheet.WaitForPreview();

        (string Id, string Name, ControlType Type)[] expected =
        [
            ("PrintPrinter", "Printer", ControlType.ComboBox),
            ("PrintPreferences", "Printer preferences", ControlType.Button),
            ("PrintCopies", "Copies", ControlType.Spinner),
            ("PrintCollate", "Collate", ControlType.CheckBox),
            ("PrintPagesAll", "Pages: All pages", ControlType.RadioButton),
            ("PrintPagesCurrent", "Pages: Current page", ControlType.RadioButton),
            ("PrintPagesRange", "Pages: Custom range", ControlType.RadioButton),
            ("PrintRange", "Page range, for example 3-7", ControlType.Edit),
            ("PrintSubset", "Print", ControlType.ComboBox),
            ("PrintSides", "Sides", ControlType.ComboBox),
            ("PrintOrientation", "Orientation", ControlType.ComboBox),
            ("PrintAutoRotate", "Rotate and centre each page", ControlType.CheckBox),
            ("PrintScale", "Scale", ControlType.ComboBox),
            ("PrintPagesPerSheet", "Pages per sheet", ControlType.ComboBox),
            ("PrintSheetOrder", "Page order", ControlType.ComboBox),
            ("PrintPaper", "Paper size", ControlType.ComboBox),
            ("PrintColour", "Colour", ControlType.ComboBox),
            ("PrintIncludeAnnotations", "Print comments and markup", ControlType.CheckBox),
            ("PrintIncludeForms", "Print form field contents", ControlType.CheckBox),
            ("PrintIncludeImage", "Print as image (for printers that have trouble)", ControlType.CheckBox),
            ("PrintPrev", "Previous page in preview", ControlType.Button),
            ("PrintNext", "Next page in preview", ControlType.Button),
        ];
        foreach (var (id, name, type) in expected)
        {
            var element = app.Require(id);
            Assert.Equal(name, element.Name);
            Assert.Equal(type, element.ControlType);
        }

        Assert.Equal(ControlType.Image, app.Require("PrintPreview").ControlType);
        Assert.StartsWith("Preview of page 1.", app.Require("PrintPreview").Name, StringComparison.Ordinal);
        Assert.StartsWith("Printing 8 pages", app.Require("PrintSummary").Name, StringComparison.Ordinal);
        Assert.Equal("Previous page in preview", app.Require("PrintPrev").Name);

        // Tab order: the options in the order the design gives, then the pager, then the dialog's buttons. Controls that are off on this queue (collate with one copy, sides on a printer
        // with one side, the page order with one page per sheet, Previous on the first sheet) are skipped by Tab, and the radio group is one stop.
        string[] order =
        [
            "PrintPrinter", "PrintPreferences", "PrintCopies", "PrintPagesAll", "PrintRange", "PrintSubset", "PrintOrientation", "PrintAutoRotate", "PrintScale", "PrintPagesPerSheet",
            "PrintPaper", "PrintColour", "PrintIncludeAnnotations", "PrintIncludeForms", "PrintIncludeImage", "PrintNext", "PrimaryButton", "CloseButton",
        ];
        app.Require("PrintPrinter").Focus();
        var seen = new List<string> { FocusedControl(app) };
        for (var i = 0; i < order.Length - 1; i++)
        {
            app.Press(VirtualKeyShort.TAB);
            seen.Add(FocusedControl(app));
        }

        Assert.Equal(order, seen);
        sheet.Cancel();
    }

    private static string FocusedControl(MikePDFApp app)
    {
        for (var element = app.Automation.FocusedElement(); element is not null; element = element.Parent)
        {
            var id = element.Properties.AutomationId.ValueOrDefault;
            if (id is not null && (id.StartsWith("Print", StringComparison.Ordinal) || id is "PrimaryButton" or "CloseButton"))
            {
                return id;
            }
        }

        return "?";
    }

    [UiFact]
    public void PRINT21_TheProgressIsAProgressBarWithAValueAndIsAnnouncedOncePerSheetAndOnceAtTheEnd()
    {
        using var app = Launch(TestPdfs.Numbered(_work, "live.pdf", 6));
        app.WaitForDocument(6);
        var sheet = OpenOnPdfQueue(app);
        sheet.SetRange("1-4");
        app.WaitFor("the summary", () => sheet.Summary == "Printing 4 pages on 4 sheets");

        // What a screen reader hears: the UIA notifications of the window (the way the tab moves are announced), one for each sheet counted and one at the end.
        var heard = new List<(string Text, string Activity)>();
        using var listening = app.ListenForNotifications(heard);

        sheet.Print();
        // A ProgressBar named for the document, whose range is the sheets of the job (its value moves with them).
        app.WaitFor("the progress bar, named for the document, with the sheets of the job as its range", () =>
            sheet.Bar is { } bar && bar.Properties.Name.ValueOrDefault == "Printing live" && bar.Patterns.RangeValue.IsSupported && bar.Patterns.RangeValue.Pattern.Maximum.ValueOrDefault >= 4, TimeSpan.FromSeconds(60));
        sheet.WaitForDone();
        Thread.Sleep(800);

        string[] events;
        lock (heard)
        {
            events = [.. heard.Where(h => h.Activity == "PrintAnnouncement").Select(h => h.Text)];
        }

        Note("PRINT-21 notifications (UIA): " + string.Join(" | ", events));
        Assert.Equal(["Printing page 1 of 4", "Printing page 2 of 4", "Printing page 3 of 4", "Printing page 4 of 4", "Sent to " + Queue + "."], events); // once per page, once at the end, nothing per band
    }

    [UiFact]
    public async Task TheWholeJobWorksFromTheKeyboardAlone()
    {
        var pdf = TestPdfs.Numbered(_work, "keys.pdf", 10);
        using var app = Launch(pdf);
        app.WaitForDocument(10);
        app.FocusPages();
        app.PressWithControl(VirtualKeyShort.KEY_P); // open
        app.WaitFor("the print sheet", () => app.Exists("PrintPrinter"));
        var sheet = new PrintSheetPage(app);
        sheet.WaitUntilReady();
        Assert.True(app.HasFocus("PrintPrinter"), "initial focus is the printer drop-down (design 11-print section 5)");

        // Tab to the range box, type 3-7, Enter.
        for (var i = 0; i < 12 && FocusedControl(app) != "PrintRange"; i++)
        {
            app.Press(VirtualKeyShort.TAB);
        }

        Assert.Equal("PrintRange", FocusedControl(app));
        app.Type("3-7");
        app.WaitFor("the summary", () => sheet.Summary == "Printing 5 pages on 5 sheets", TimeSpan.FromSeconds(5));
        app.WaitFor("the printer chosen", () => sheet.Printer.Length > 0);
        if (sheet.Printer != Queue)
        {
            sheet.ChoosePrinter(Queue);
            app.Require("PrintRange").Focus();
        }

        app.Press(VirtualKeyShort.ENTER);
        app.WaitFor("the sheet to close on Enter", () => !app.Exists("PrintPrinter"), TimeSpan.FromSeconds(10));
        sheet.WaitForDone();
        await using var printed = await Lab.OpenAsync(Printed(1));
        Assert.Equal(5, printed.PageCount);
    }

    [UiFact]
    public void ANarrowWindowStacksThePreviewAboveTheOptionsAndAVeryNarrowOneHidesItBehindAToggle()
    {
        using var app = Launch(TestPdfs.Numbered(_work, "narrow.pdf", 3));
        app.WaitForDocument(3);
        app.ResizeTo(640, 800);
        var sheet = OpenOnPdfQueue(app);
        sheet.WaitForPreview();
        app.Capture("print-sheet-narrow");
        var preview = app.Require("PrintPreview").BoundingRectangle;
        var printer = app.Require("PrintPrinter").BoundingRectangle; // (scrolled up out of view in a short dialog, but still as wide as the options)
        Assert.True(preview.Left >= printer.Left && preview.Right <= printer.Right, $"stacked: the preview sits over the options, not beside them (preview {preview}, printer {printer})");
        sheet.Cancel();

        app.ResizeTo(480, 800);
        sheet.Open();
        sheet.WaitUntilReady();
        app.WaitFor("the preview toggle", () => app.Find("PrintShowPreview") is { IsOffscreen: false });
        Assert.StartsWith("Show preview", app.Require("PrintShowPreview").Name, StringComparison.Ordinal);
        sheet.Cancel();
    }

    [UiFact]
    public void ThePrintersOwnPreferencesOpenFromTheSheetAndCancelLeavesTheSheetAsItWas()
    {
        using var app = Launch(TestPdfs.Numbered(_work, "prefs.pdf", 2));
        app.WaitForDocument(2);
        var sheet = OpenOnPdfQueue(app);
        var before = sheet.Selected("PrintPaper");
        app.Click("PrintPreferences");
        AutomationElement? driver = null;
        const string DriverDialog = "Microsoft Print to PDF Document Properties";
        app.WaitFor("the driver's own dialog", () => (driver = app.Automation.GetDesktop().FindFirstDescendant(cf => cf.ByName(DriverDialog))) is not null, TimeSpan.FromSeconds(20));
        Note("preferences dialog: " + DriverDialog);
        driver!.FindFirstDescendant(cf => cf.ByName("Cancel"))!.AsButton().Invoke(); // (the driver's dialog is not ours: its own Cancel, not a key sent to the window behind it)
        app.WaitFor("the driver's dialog closed", () => app.Automation.GetDesktop().FindFirstDescendant(cf => cf.ByName(DriverDialog)) is null);
        Assert.True(app.Exists("PrintPrinter"));
        Assert.Equal(before, sheet.Selected("PrintPaper"));
        sheet.Cancel();
    }
}

/// <summary>The jobs the spooler holds for a queue right now.</summary>
internal static partial class PrintQueue
{
    public static int JobsIn(string printer)
    {
        if (!OpenPrinterW(printer, out var handle, 0))
        {
            return -1;
        }

        try
        {
            GetPrinterW(handle, 2, 0, 0, out var needed);
            if (needed == 0)
            {
                return -1;
            }

            var buffer = Marshal.AllocHGlobal((int)needed);
            try
            {
                return GetPrinterW(handle, 2, buffer, needed, out _) ? Marshal.ReadInt32(buffer, 128) : -1; // PRINTER_INFO_2W: 13 pointers, then Attributes, Priority, DefaultPriority, StartTime, UntilTime, Status, cJobs
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            ClosePrinter(handle);
        }
    }

    [LibraryImport("winspool.drv", EntryPoint = "OpenPrinterW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenPrinterW(string name, out nint handle, nint defaults);

    [LibraryImport("winspool.drv", EntryPoint = "GetPrinterW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetPrinterW(nint handle, uint level, nint buffer, uint size, out uint needed);

    [LibraryImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClosePrinter(nint handle);
}
