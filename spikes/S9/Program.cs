using System.Diagnostics;
using System.Text.Json;
using Microsoft.Graphics.Canvas;

namespace S9;

internal static class Program
{
    public static string Out = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "out");

    [STAThread]
    public static int Main(string[] args)
    {
        Out = Path.GetFullPath(Environment.GetEnvironmentVariable("S9_OUT") ?? Out);
        Directory.CreateDirectory(Out);
        var cmd = args.Length > 0 ? args[0] : "smoke";
        try
        {
            switch (cmd)
            {
                case "smoke": Smoke(); break;
                case "overlay": OverlayBench.Run(args.Skip(1).ToArray()); break;
                case "hidden": HiddenFlag.Run(); break;
                case "hiddencost": HiddenFlag.RunCost(); break;
                case "rebase": Rebase.Run(); break;
                case "highlight": HighlightCompare.Run(); break;
                default: Console.Error.WriteLine("unknown command " + cmd); return 2;
            }
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        return 0;
    }

    public static void WriteJson(string name, object o)
    {
        File.WriteAllText(Path.Combine(Out, name), JsonSerializer.Serialize(o, new JsonSerializerOptions { WriteIndented = true }));
    }

    static void Smoke()
    {
        Console.WriteLine("adapters:");
        foreach (var a in Gpu.Adapters()) Console.WriteLine($"  [{a.Index}] {a.Name} vendor=0x{a.Vendor:X} dedicated={a.DedicatedMB}MB flags={a.Flags}");
        var b = BaseDoc.Build(1);
        using var doc = new PdfDoc(b.Bytes);
        var page = Pdfium.FPDF_LoadPage(doc.Handle, 0);
        var img = PdfRender.Page(page, 96.0 / 72, 0);
        Imaging.SavePng(Path.Combine(Out, "smoke-base.png"), img);
        Console.WriteLine($"base page {img.W}x{img.H} hash {Imaging.Hash(img)}");
        foreach (var idx in new[] { -2, 0, 1, -1 })
        {
            try
            {
                var dev = idx == -2 ? CanvasDevice.GetSharedDevice() : Gpu.Create(idx);
                using var rt = new CanvasRenderTarget(dev, 64, 64, 96);
                using (var ds = rt.CreateDrawingSession()) ds.Clear(Windows.UI.Color.FromArgb(255, 10, 20, 30));
                var px = rt.GetPixelBytes(0, 0, 1, 1);
                Console.WriteLine($"device {idx}: ok px={px[0]},{px[1]},{px[2]},{px[3]} maxBitmap={dev.MaximumBitmapSizeInPixels}");
            }
            catch (Exception ex) { Console.WriteLine($"device {idx}: FAILED {ex.Message}"); }
        }
    }
}
