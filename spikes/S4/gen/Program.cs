using System.Security.Cryptography;
using System.Text.Json;

namespace S4;

/// <summary>Per-case context: a freshly opened document, its page 0, and a log of every API return code.</summary>
public sealed unsafe class Ctx
{
    public PdfDoc D = null!;
    public IntPtr Page;
    public List<string> Log = new();
    public Dictionary<string, object> Exp = new();
    public void L(string what, object rc) => Log.Add($"{what} -> {rc}");
    public IntPtr Form => D.Form;
}

public sealed class Case
{
    public string Id = "", Group = "", Title = "", Input = "";
    public Func<Ctx, bool>? Apply;                 // returns false when the API cannot do it
    public Func<PdfFile>? Raw;                     // Quire-authored incremental update (no PDFium editing)
    public Func<PdfFile, RawAnnots.Result>? RawFn;
    public string Note = "";
}

public static unsafe class Program
{
    static string Out = "";

    public static int Main(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : Path.Combine(FindRoot(), "spikes", "S4", "out");
        Out = outDir;
        Directory.CreateDirectory(Path.Combine(outDir, "inputs"));
        Directory.CreateDirectory(Path.Combine(outDir, "files"));
        PdfDoc.Init();

        // ---- fixtures
        var baseF = Fx.BuildBase(false);
        var formsF = Fx.BuildForms(false);
        var signedBaseU = Fx.BuildBase(true);
        var signedBase = Fx.Sign(signedBaseU);
        var signedFormsU = Fx.BuildForms(true);
        var signedForms = Fx.Sign(signedFormsU);
        var inputs = new Dictionary<string, PdfFile>
        {
            ["base"] = baseF, ["forms"] = formsF, ["signed-base"] = signedBase, ["signed-forms"] = signedForms
        };
        inputs["corpus-forms"] = new PdfFile { Bytes = File.ReadAllBytes(Path.Combine(FindRoot(), "spikes", "_shared", "corpus", "pdfium-resources", "multiple_form_types.pdf")) };
        var inputInfo = new Dictionary<string, object>();
        foreach (var (k, v) in inputs)
        {
            File.WriteAllBytes(Path.Combine(outDir, "inputs", k + ".pdf"), v.Bytes);
            inputInfo[k] = new Dictionary<string, object>
            {
                ["file"] = $"inputs/{k}.pdf", ["bytes"] = v.Bytes.Length, ["sha256"] = Convert.ToHexString(SHA256.HashData(v.Bytes)),
                ["byteRange"] = v.SigByteRange
            };
        }
        // self-check of the signed fixtures
        foreach (var k in new[] { "signed-base", "signed-forms" })
        {
            var f = inputs[k];
            var (ok, msg) = Fx.VerifyCms(f.Bytes, f.SigByteRange, f.SigCms!);
            Console.WriteLine($"[fixture] {k}: {msg} ({ok})");
            ((Dictionary<string, object>)inputInfo[k])["cmsVerify"] = msg;
        }

        var cases = Cases.All(baseF);
        var manifest = new List<Dictionary<string, object>>();
        foreach (var c in cases)
        {
            var m = new Dictionary<string, object> { ["id"] = c.Id, ["group"] = c.Group, ["title"] = c.Title, ["input"] = c.Input, ["note"] = c.Note };
            try
            {
                if (c.RawFn != null)
                {
                    var r = c.RawFn(baseF);
                    File.WriteAllBytes(Path.Combine(outDir, "files", c.Id + ".inc.pdf"), r.File.Bytes);
                    m["outputs"] = new Dictionary<string, string> { ["inc"] = $"files/{c.Id}.inc.pdf" };
                    m["exp"] = new Dictionary<string, object>
                    {
                        ["subtype"] = r.Subtype, ["rect"] = new[] { r.Rect.left, r.Rect.bottom, r.Rect.right, r.Rect.top }, ["contents"] = r.Contents
                    };
                    m["creatable"] = true; m["mode"] = "quire-raw";
                    m["log"] = new[] { r.Note };
                    // full copy = reload the Quire-authored file in PDFium and FPDF_NO_INCREMENTAL it
                    using var d = PdfDoc.Open(r.File.Bytes, true, out var err);
                    if (d == null) { m["fullError"] = err; }
                    else
                    {
                        d.LoadPage(0);
                        var full = d.Save(P.FPDF_NO_INCREMENTAL, out var e2);
                        if (full == null) m["fullError"] = e2;
                        else
                        {
                            File.WriteAllBytes(Path.Combine(outDir, "files", c.Id + ".full.pdf"), full);
                            ((Dictionary<string, string>)m["outputs"])["full"] = $"files/{c.Id}.full.pdf";
                        }
                    }
                }
                else
                {
                    var outs = new Dictionary<string, string>();
                    m["outputs"] = outs;
                    bool first = true;
                    var modeList = new List<(string, uint, bool)> { ("inc", P.FPDF_INCREMENTAL, false), ("full", P.FPDF_NO_INCREMENTAL, false) };
                    if (c.Group == "annot") { modeList.Add(("incR", P.FPDF_INCREMENTAL, true)); modeList.Add(("fullR", P.FPDF_NO_INCREMENTAL, true)); }
                    foreach (var (mode, flag, renderFirst) in modeList)
                    {
                        var ctx = new Ctx();
                        var input = inputs[c.Input];
                        ctx.D = PdfDoc.Open(input.Bytes, true, out var err);
                        if (ctx.D == null) { m["error"] = err; break; }
                        using (ctx.D)
                        {
                            ctx.Page = ctx.D.LoadPage(0);
                            bool creatable = c.Apply!(ctx);
                            if (first)
                            {
                                m["creatable"] = creatable; m["log"] = ctx.Log.ToList(); m["exp"] = ctx.Exp; m["mode"] = "pdfium-api";
                                first = false;
                            }
                            if (!creatable) break;
                            if (renderFirst) { var (rw, rh, _) = PdfDoc.Render(ctx.Page, 1.0, true); ctx.Log.Add($"rendered page ({rw}x{rh}, FPDF_ANNOT) before save"); }
                            var bytes = ctx.D.Save(flag, out var e);
                            if (bytes == null) { m[$"saveError_{mode}"] = e; continue; }
                            var rel = $"files/{c.Id}.{mode}.pdf";
                            File.WriteAllBytes(Path.Combine(outDir, rel), bytes);
                            outs[mode] = rel;
                        }
                    }
                }
            }
            catch (Exception ex) { m["exception"] = ex.ToString(); }
            manifest.Add(m);
            Console.WriteLine($"[{c.Id}] {c.Title}: creatable={m.GetValueOrDefault("creatable")} outputs={string.Join(",", (((Dictionary<string, string>?)m.GetValueOrDefault("outputs"))?.Keys.ToArray() ?? Array.Empty<string>()))}");
        }
        var root = new Dictionary<string, object>
        {
            ["generated"] = DateTime.UtcNow.ToString("o"), ["pdfiumDll"] = P.FindDll(),
            ["inputs"] = inputInfo, ["cases"] = manifest
        };
        File.WriteAllText(Path.Combine(outDir, "manifest.json"), JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("wrote manifest.json");
        return 0;
    }

    public static string FindRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "spikes"))) d = d.Parent;
        return d?.FullName ?? throw new Exception("repo root not found");
    }
}
