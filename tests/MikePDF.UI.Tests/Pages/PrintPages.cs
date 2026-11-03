using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.WindowsAPI;
using MikePDF.Broker;
using MikePDF.Core.Geometry;
using MikePDF.Core.Printing;
using MikePDF.Integration.Tests.Printing;
using MikePDF.Printing;
using MikePDF.Render.Imaging;

namespace MikePDF.UI.Tests.Pages;

/// <summary>
/// The print sheet as a person meets it (docs/design/screens/11-print.md): open it with Ctrl+P, choose a printer and the options, press Print, and watch the strip. The sheet's automation ids are
/// known here and nowhere else (ADR-0009, design 11-print section 6).
/// </summary>
public sealed class PrintSheetPage(MikePDFApp app)
{
    public const string PdfQueue = "Microsoft Print to PDF";

    public bool IsOpen => app.Exists("PrintPrinter"); // a panel has no automation peer: the sheet is open when its first control is there

    public void Open()
    {
        app.Chord(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_P);
        var banner = string.Empty;
        try
        {
            app.WaitFor("the print sheet", () =>
            {
                if (app.Find("Banner") is { IsOffscreen: false } shown && string.Join(" / ", shown.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)).Select(t => t.Properties.Name.ValueOrDefault)) is { Length: > 0 } words)
                {
                    banner = words;
                }

                return IsOpen;
            }, TimeSpan.FromSeconds(15));
        }
        catch (TimeoutException)
        {
            // Say what the window said instead (a banner with the failure, which goes after a few seconds), so the report is more than a timeout.
            throw new TimeoutException("The print sheet did not open. The banner said: [" + banner + "]; the dialog is " + (app.Exists("PrintDialog") ? "there" : "not there"));
        }
    }

    /// <summary>Waits until the printers have been listed, one is chosen, and the preview has had its first picture.</summary>
    public void WaitUntilReady() => app.WaitFor("the print sheet to know its printer and the pages", () => Printer.Length > 0 && PrintButton is { IsEnabled: true }, TimeSpan.FromSeconds(30));

    public void WaitForPreview() => app.WaitFor("the preview picture", () => PreviewName.StartsWith("Preview of", StringComparison.Ordinal), TimeSpan.FromSeconds(60));

    private static string NameOf(AutomationElement? element) => element?.Properties.Name.ValueOrDefault ?? string.Empty;

    public string PreviewName => NameOf(app.Find("PrintPreview"));

    public string Printer => app.Find("PrintPrinter") is { } box ? box.AsComboBox().SelectedItem?.Name ?? string.Empty : string.Empty;

    public void ChoosePrinter(string name) => Choose("PrintPrinter", name);

    public void Choose(string automationId, string item)
    {
        app.WaitFor("the " + automationId + " control to be ready", () => app.Find(automationId) is { IsEnabled: true });
        var combo = app.Require(automationId).AsComboBox();
        if (combo.SelectedItem?.Name != item)
        {
            combo.Select(item);
        }

        app.WaitFor(automationId + " = " + item, () => combo.SelectedItem?.Name == item);
    }

    public string Selected(string automationId) => app.Require(automationId).AsComboBox().SelectedItem?.Name ?? string.Empty;

    public bool IsEnabled(string automationId) => app.Require(automationId).IsEnabled;

    public void SetRange(string text)
    {
        var box = app.Require("PrintRange");
        box.Focus();
        box.Patterns.Value.Pattern.SetValue(text);
        Thread.Sleep(300);
    }

    public string RangeText => app.Require("PrintRange").Patterns.Value.Pattern.Value.ValueOrDefault ?? string.Empty;

    /// <summary>The range error as the sheet shows it, or empty when there is none.</summary>
    public string RangeError => app.Find("PrintRangeError") is { IsOffscreen: false } error ? NameOf(error) : string.Empty;

    public string Summary => NameOf(app.Find("PrintSummary"));

    public bool IsChecked(string automationId) => app.Require(automationId).AsCheckBox().IsChecked == true;

    public void Check(string automationId, bool on)
    {
        var box = app.Require(automationId).AsCheckBox();
        if ((box.IsChecked == true) != on)
        {
            box.Toggle();
        }
    }

    public void SetCopies(int copies) => app.Require("PrintCopies").Patterns.RangeValue.Pattern.SetValue(copies);

    public void ChooseScope(string automationId) => app.Require(automationId).AsRadioButton().IsChecked = true;

    public bool IsScope(string automationId) => app.Require(automationId).AsRadioButton().IsChecked;

    public Button? PrintButton => app.Window.FindFirstDescendant(cf => cf.ByAutomationId("PrimaryButton"))?.AsButton();

    public Button? CancelButton => app.Window.FindFirstDescendant(cf => cf.ByAutomationId("CloseButton"))?.AsButton();

    public void Print()
    {
        app.WaitFor("the Print button to be on", () => PrintButton is { IsEnabled: true });
        PrintButton!.Invoke();
        app.WaitFor("the print sheet to close", () => !IsOpen);
    }

    public void Cancel()
    {
        CancelButton!.Invoke();
        app.WaitFor("the print sheet to close", () => !IsOpen);
    }

    // ---- the strip ----

    public bool StripShown => app.Find("PrintProgress") is { IsOffscreen: false };

    public string StripStatus => NameOf(app.Find("PrintStatus"));

    public string StripDetail => NameOf(app.Find("PrintDetail"));

    public AutomationElement? Bar => app.Find("PrintProgressBar");

    public void CancelJob() => app.Click("PrintCancel");

    public void WaitForDone(string printer = PdfQueue, TimeSpan? timeout = null) =>
        app.WaitFor("the strip to say it was sent to " + printer, () => StripStatus == "Sent to " + printer + ".", timeout ?? TimeSpan.FromMinutes(3));
}

/// <summary>
/// The sandboxed engine as the print journeys need it: to read the PDF a print wrote (page count, page sizes) and to draw pages of it and of the source for the comparison of journey 11. It is deployed
/// the way the integration tests deploy it (a private copy, an AppContainer profile of its own, removed afterwards).
/// </summary>
public sealed class EngineLab : IDisposable
{
    private readonly string _deploy;
    private readonly AppContainerProfile _profile;

    public EngineLab()
    {
        var root = RepositoryRoot();
        var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";
        var source = Environment.GetEnvironmentVariable("MIKEPDF_ENGINE_DIR");
        if (string.IsNullOrEmpty(source) || !Directory.Exists(source))
        {
            source = Path.Combine(root, "build", "out", "cmake-" + arch, "bin", "Release");
        }

        foreach (var file in new[] { "MikePDF.Engine.exe", "pdfium.dll" })
        {
            if (!File.Exists(Path.Combine(source, file)))
            {
                throw new FileNotFoundException(file + " not found in " + source + ". Build the native half first: ./build.ps1 -SkipManaged", file);
            }
        }

        _deploy = Path.Combine(Path.GetTempPath(), "mikepdf-ui-engine-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_deploy);
        _profile = AppContainerProfile.CreateOrOpen("MikePDF.UIT." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N")[..10], "MikePDF UI tests", "Zero-capability AppContainer for the print journeys' file checks");
        EngineDeployment.EnsureAppContainerAccess(_deploy, _profile);
        foreach (var file in new[] { "MikePDF.Engine.exe", "pdfium.dll" })
        {
            File.Copy(Path.Combine(source, file), Path.Combine(_deploy, file));
        }
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MikePDF.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("MikePDF.slnx not found above " + AppContext.BaseDirectory);
    }

    /// <summary>Opens a PDF in a fresh engine and reads its page sizes.</summary>
    public async Task<PdfFile> OpenAsync(string path)
    {
        var supervisor = await EngineSupervisor.StartAsync(new EngineSupervisorOptions { EnginePath = Path.Combine(_deploy, "MikePDF.Engine.exe"), Profile = _profile, SlotCount = 8, SlotSize = 4 << 20 });
        var opened = await supervisor.OpenDocumentAsync(path);
        if (!opened.IsOk || opened.Value is null)
        {
            await supervisor.DisposeAsync();
            throw new InvalidOperationException($"The engine could not open {path}: {opened.Status} {opened.Detail}");
        }

        var document = opened.Value;
        var sizes = await document.GetPageSizesAsync(0, document.PageCount);
        if (!sizes.IsOk || sizes.Value is null)
        {
            await supervisor.DisposeAsync();
            throw new InvalidOperationException("No page sizes: " + sizes.Status);
        }

        return new PdfFile(supervisor, document, [.. sizes.Value.Widths.Zip(sizes.Value.Heights, (w, h) => new SizeD(w, h))]);
    }

    public void Dispose()
    {
        try
        {
            EngineDeployment.RevokeAppContainerAccess(_deploy, _profile.Sid);
            Directory.Delete(_deploy, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The engine process that was just stopped may still hold a file for a moment; the temp folder is cleaned eventually.
        }

        try
        {
            _profile.Delete();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Another run's sweep, or already gone.
        }
    }
}

/// <summary>A PDF open in an engine: its page count and sizes, and the same comparison the print tests make between a printed page and the page it came from.</summary>
public sealed class PdfFile(EngineSupervisor supervisor, SupervisedDocument document, IReadOnlyList<SizeD> sizes) : IAsyncDisposable
{
    public const double SizeTolerance = 1.5;

    public int PageCount => sizes.Count;

    public IReadOnlyList<SizeD> Sizes => sizes;

    public SupervisedDocument Document => document;

    public async ValueTask DisposeAsync()
    {
        await document.DisposeAsync();
        await supervisor.DisposeAsync();
    }

    /// <summary>The printable area of the Microsoft Print to PDF queue for A4 portrait paper, in its own pixels (what the printer reports for a job; nothing is printed).</summary>
    public static PaperArea PdfQueueA4()
    {
        var opened = GdiPrintDeviceFactory.Instance.Open(new PrinterJobSettings(PrintSheetPage.PdfQueue, new PrintOptions { PaperSizeId = 9 }, Landscape: false));
        using (opened.Device)
        {
            return opened.Device.Paper;
        }
    }

    /// <summary>
    /// SSIM of one placed page of this (printed) file against the engine's render of <paramref name="source"/>'s page under the same transform, both at the printer's resolution and box filtered by 4
    /// (ADR-0011 decision 5). <paramref name="cellX"/>, <paramref name="cellY"/> and <paramref name="cell"/> place the page in a cell of a sheet with several pages; without them the cell is the paper.
    /// </summary>
    public async Task<double> SsimAsync(int printedPage, PdfFile source, int sourcePage, PaperArea paper, PrintScaling scaling, double percent, bool autoRotate, int cellX = 0, int cellY = 0, PaperArea? cell = null)
    {
        var size = source.Sizes[sourcePage];
        var placement = PrintLayout.Place(size, cell ?? paper, scaling, percent, autoRotate).Offset(cellX, cellY);
        const int factor = 4;
        var w = placement.Width / factor * factor;
        var h = placement.Height / factor * factor;
        const uint flags = 7; // printing mode, annotations and form fields, as the sheet's defaults send them
        var printed = await PageImages.DownscaledAsync(document, printedPage, paper.WidthPx, paper.HeightPx, 0, flags, placement.X, placement.Y, w, h, factor);
        var reference = await PageImages.DownscaledAsync(source.Document, sourcePage, placement.Width, placement.Height, placement.Rotation, flags, 0, 0, w, h, factor);
        return Ssim.Compare(reference, printed).Ssim;
    }
}
