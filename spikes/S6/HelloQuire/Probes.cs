using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Graphics.Canvas;
using Microsoft.Windows.AI;
using Microsoft.Windows.AI.Imaging;
using Microsoft.Windows.AI.Text;
using Microsoft.Windows.Search.AppContentIndex;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace HelloQuire;

/// <summary>S6.4 / S5.1 runtime probes. Every call is wrapped so the exact state or exception is recorded verbatim.</summary>
internal static class Probes
{
    [DllImport("kernel32.dll")] static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern bool IsWow64Process2(nint h, out ushort proc, out ushort native);

    private static string Ex(Exception e) => $"{e.GetType().FullName} HRESULT=0x{e.HResult:X8}: {e.Message.Replace('\r', ' ').Replace('\n', ' ')}";

    private static async Task<string> Step(string name, Func<Task<string>> f)
    {
        try { return $"[{name}] {await f()}\n"; }
        catch (Exception e) { return $"[{name}] EXCEPTION {Ex(e)}\n"; }
    }

    public static async Task<string> RunAllAsync(MainWindow w)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"== S6 probe {DateTime.Now:s} ==");
        IsWow64Process2(GetCurrentProcess(), out var pm, out var nm);
        sb.AppendLine($"ProcessArchitecture={RuntimeInformation.ProcessArchitecture} OSArchitecture={RuntimeInformation.OSArchitecture} IsWow64Process2(processMachine=0x{pm:X}, nativeMachine=0x{nm:X}) OS={Environment.OSVersion.Version} .NET={RuntimeInformation.FrameworkDescription}");
        sb.Append(await Step("package identity", () => Task.FromResult(Windows.ApplicationModel.Package.Current.Id.FullName + " | family=" + Windows.ApplicationModel.Package.Current.Id.FamilyName + " | installed=" + Windows.ApplicationModel.Package.Current.InstalledPath)));
        sb.Append(await Step("WindowsAppRuntime info", () => Task.FromResult(Microsoft.Windows.ApplicationModel.WindowsAppRuntime.RuntimeInfo.AsString)));

        // ---- Windows AI feature detection (Microsoft.Windows.AI.* from Microsoft.WindowsAppSDK.AI 2.5.5) ----
        sb.Append(await Step("LanguageModel.GetReadyState", () => Task.FromResult(LanguageModel.GetReadyState().ToString())));
        sb.Append(await Step("TextRecognizer.GetReadyState", () => Task.FromResult(TextRecognizer.GetReadyState().ToString())));
        // Limited Access Feature unlock without a token (feature ids read from the framework DLL strings)
        sb.Append(await Step("LAF unlock com.microsoft.windows.ai.languagemodel (empty token)", () => Task.FromResult(
            Windows.ApplicationModel.LimitedAccessFeatures.TryUnlockFeature("com.microsoft.windows.ai.languagemodel", "", "S6 spike has no token").Status.ToString())));
        sb.Append(await Step("LanguageModel.GetReadyState (after LAF attempt)", () => Task.FromResult(LanguageModel.GetReadyState().ToString())));
        var lmState = LanguageModel.GetReadyState();
        if (lmState != AIFeatureReadyState.NotReady)
            sb.Append(await Step("LanguageModel.CreateAsync (state != NotReady, so no download is triggered)", async () => { using var m = await LanguageModel.CreateAsync(); return "created"; }));
        else sb.AppendLine("[LanguageModel.CreateAsync] SKIPPED: state is NotReady and a call could start a model download; no download was authorised for this spike.");
        var trState = TextRecognizer.GetReadyState();
        if (trState == AIFeatureReadyState.Ready)
            sb.Append(await Step("TextRecognizer OCR", async () =>
            {
                var (bgra, ww, hh) = Pdfium.RenderSample(2.0);
                using var sbmp = MakeSoftwareBitmap(bgra, ww, hh);
                using var rec = await TextRecognizer.CreateAsync();
                var res = rec.RecognizeTextFromImage(Microsoft.Graphics.Imaging.ImageBuffer.CreateForSoftwareBitmap(sbmp));
                return string.Join(" | ", res.Lines.Select(l => l.Text));
            }));
        else if (trState != AIFeatureReadyState.NotReady)
            sb.Append(await Step("TextRecognizer.CreateAsync (state != NotReady)", async () => { using var r = await TextRecognizer.CreateAsync(); return "created"; }));
        else sb.AppendLine("[TextRecognizer.CreateAsync] SKIPPED: state NotReady (may trigger model download; not authorised).");

        // ---- Windows.Media.Ocr fallback ----
        sb.Append(await Step("Windows.Media.Ocr available languages", () => Task.FromResult(string.Join(", ", OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag)))));
        sb.Append(await Step("Windows.Media.Ocr real OCR of PDFium-rendered text", async () =>
        {
            var (bgra, ww, hh) = Pdfium.RenderSample(2.0);
            using var sbmp = MakeSoftwareBitmap(bgra, ww, hh);
            var engine = OcrEngine.TryCreateFromLanguage(new Language("en-US")) ?? OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine == null) return "no OcrEngine could be created";
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = await engine.RecognizeAsync(sbmp);
            sw.Stop();
            return $"engine={engine.RecognizerLanguage.LanguageTag} ms={sw.ElapsedMilliseconds} lines={r.Lines.Count} text=\"{r.Text.Replace('\n', '|')}\"";
        }));

        // ---- App Content Search ----
        sb.Append(await Step("AppContentIndexer static surface (reflection)", () => Task.FromResult(string.Join(", ",
            typeof(AppContentIndexer).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static).Select(m => m.Name).Distinct()))));
        sb.Append(await Step("LAF unlock com.microsoft.windows.ai.appcontentindexer (empty token)", () => Task.FromResult(
            Windows.ApplicationModel.LimitedAccessFeatures.TryUnlockFeature("com.microsoft.windows.ai.appcontentindexer", "", "S6 spike has no token").Status.ToString())));
        sb.Append(await Step("AppContentIndexer.GetOrCreateIndex(\"s6probe\") without token", () =>
        {
            var r = AppContentIndexer.GetOrCreateIndex("s6probe");
            string s = $"Succeeded={r.Succeeded} Status={r.Status} ExtendedError=0x{(uint)r.ExtendedError.HResult:X8} {r.ExtendedError.Message}";
            r.Indexer?.Dispose();
            return Task.FromResult(s);
        }));

        // ---- Win2D headless pixel read-back (independent of what is visible on screen) ----
        sb.Append(await Step("Win2D CanvasRenderTarget read-back", () =>
        {
            var dev = CanvasDevice.GetSharedDevice();
            using var rt = new CanvasRenderTarget(dev, 64, 64, 96);
            using (var ds = rt.CreateDrawingSession()) { ds.Clear(Microsoft.UI.Colors.Blue); ds.FillCircle(32, 32, 16, Microsoft.UI.Colors.Red); }
            var px = rt.GetPixelColors();
            return Task.FromResult($"corner={px[0]} centre={px[32 * 64 + 32]} device={dev.GetType().Name} debugLevel={CanvasDevice.DebugLevel}");
        }));

        // ---- S6.5: is the .pdf file type association visible to the shell? ----
        sb.Append(await Step("Launcher.FindFileHandlersAsync(.pdf)", async () =>
        {
            var apps = await Windows.System.Launcher.FindFileHandlersAsync(".pdf");
            return apps.Count + " handlers: " + string.Join("; ", apps.Select(a => a.DisplayInfo.DisplayName + " [" + a.PackageFamilyName + "]"));
        }));
        sb.Append(await Step("Launcher.FindFileHandlersAsync(.txt) (control)", async () =>
        {
            var apps = await Windows.System.Launcher.FindFileHandlersAsync(".txt");
            return apps.Count + " handlers: " + string.Join("; ", apps.Select(a => a.DisplayInfo.DisplayName));
        }));

        // ---- S5.1 ink probe ----
        sb.AppendLine("== S5.1 ink probe ==");
        try { sb.Append(InkProbe.Run(CanvasDevice.GetSharedDevice())); }
        catch (Exception e) { sb.AppendLine("InkProbe EXCEPTION " + Ex(e)); }
        return sb.ToString();
    }

    private static SoftwareBitmap MakeSoftwareBitmap(byte[] bgra, int w, int h)
    {
        var dw = new DataWriter();
        dw.WriteBytes(bgra);
        return SoftwareBitmap.CreateCopyFromBuffer(dw.DetachBuffer(), BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Premultiplied);
    }
}
