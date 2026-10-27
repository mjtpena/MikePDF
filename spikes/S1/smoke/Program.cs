// S1 runtime smoke test: P/Invoke the pinned x64 prebuilt pdfium.dll and exercise the load,
// render, text, bookmark, signature and save APIs through a custom (byte-array) file-access
// callback, so no file path is ever handed to PDFium. This shows the APIs work, not just that
// they exist. Timings are informational only (Stopwatch, cold process): this is not a benchmark.
//
//   dotnet run -c Release -- [--dll <pdfium.dll>] [--corpus <dir>] [--out <dir>]
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Smoke;
using static Smoke.Native;

internal static unsafe class Program
{
static int Main()
{
string repo = FindRepoRoot();
string dllPath = Arg("--dll") ?? Path.Combine(repo, "spikes", "_shared", "pdfium", "pdfium-win-x64", "bin", "pdfium.dll");
string corpus = Arg("--corpus") ?? Path.Combine(repo, "spikes", "_shared", "corpus", "pdfium-resources");
string outDir = Arg("--out") ?? Path.Combine(repo, "spikes", "S1", "out");
Directory.CreateDirectory(outDir);

NativeLibrary.SetDllImportResolver(typeof(Native).Assembly,
    (name, _, _) => name == "pdfium" ? NativeLibrary.Load(dllPath) : IntPtr.Zero);

string[] fixtures =
{
    "hello_world.pdf", "rotated_text.pdf", "bookmarks.pdf", "text_form.pdf",
    "annotation_highlight_long_content.pdf", "two_signatures.pdf",
};

var report = new Dictionary<string, object?>
{
    ["process_arch"] = RuntimeInformation.ProcessArchitecture.ToString(),
    ["os_arch"] = RuntimeInformation.OSArchitecture.ToString(),
    ["runtime"] = RuntimeInformation.FrameworkDescription,
    ["dll"] = dllPath,
    ["dll_file_size"] = new FileInfo(dllPath).Length,
};
Console.WriteLine($"process={RuntimeInformation.ProcessArchitecture} os={RuntimeInformation.OSArchitecture} {RuntimeInformation.FrameworkDescription}");
Console.WriteLine($"dll={dllPath}");

var sw = Stopwatch.StartNew();
LibraryConfig cfg = new() { Version = 2 };       // version 2 = oldest non-deprecated; AGG renderer, default fonts
FPDF_InitLibraryWithConfig(&cfg);
report["init_ms"] = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
Console.WriteLine($"FPDF_InitLibraryWithConfig ok ({report["init_ms"]} ms)");

int failures = 0;
var results = new List<Dictionary<string, object?>>();
foreach (string name in fixtures)
{
    var r = new Dictionary<string, object?> { ["file"] = name };
    results.Add(r);
    try { RunFixture(name, r); }
    catch (Exception e) { r["error"] = e.ToString(); failures++; Console.WriteLine($"  FAIL {name}: {e.Message}"); }
}

// Negative case: garbage bytes must fail with a typed error, not a crash.
{
    using var bad = new MemDoc(Encoding.ASCII.GetBytes("this is not a pdf"));
    nint doc = bad.Open();
    uint err = FPDF_GetLastError();
    report["garbage_open"] = new { handle_is_null = doc == 0, last_error = err, meaning = "FPDF_ERR_FORMAT=3" };
    Console.WriteLine($"garbage input: handle={(doc == 0 ? "null" : "NON-NULL")} FPDF_GetLastError={err}");
    if (doc != 0) { FPDF_CloseDocument(doc); failures++; }
}

FPDF_DestroyLibrary();
Console.WriteLine("FPDF_DestroyLibrary ok");
report["fixtures"] = results;
report["failures"] = failures;
File.WriteAllText(Path.Combine(outDir, "smoke.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(failures == 0 ? "SMOKE PASS" : $"SMOKE FAIL ({failures})");
return failures == 0 ? 0 : 1;

// ---------------------------------------------------------------------------------------

void RunFixture(string name, Dictionary<string, object?> r)
{
    byte[] bytes = File.ReadAllBytes(Path.Combine(corpus, name));
    r["bytes"] = bytes.Length;
    var t = Stopwatch.StartNew();
    using var src = new MemDoc(bytes);
    nint doc = src.Open();
    if (doc == 0) throw new InvalidOperationException($"FPDF_LoadCustomDocument failed, last error {FPDF_GetLastError()}");
    r["load_ms"] = Ms(t);
    try
    {
        int pages = FPDF_GetPageCount(doc);
        FsSizeF sz = default;
        int sizeOk = FPDF_GetPageSizeByIndexF(doc, 0, &sz);
        r["pages"] = pages; r["page0_size_pt"] = $"{sz.Width}x{sz.Height}"; r["page_size_ok"] = sizeOk;

        // --- render page 0 into a caller-owned BGRA buffer (FPDFBitmap_CreateEx)
        t.Restart();
        nint page = FPDF_LoadPage(doc, 0);
        if (page == 0) throw new InvalidOperationException("FPDF_LoadPage failed");
        const float scale = 96f / 72f;
        int w = (int)MathF.Ceiling(sz.Width * scale), h = (int)MathF.Ceiling(sz.Height * scale);
        int stride = w * 4;
        byte* buf = (byte*)NativeMemory.AllocZeroed((nuint)(stride * h));
        nint bmp = FPDFBitmap_CreateEx(w, h, 4 /* BGRA */, buf, stride);
        FPDFBitmap_FillRect(bmp, 0, 0, w, h, 0xFFFFFFFF);
        FPDF_RenderPageBitmap(bmp, page, 0, 0, w, h, 0, 0x01 /* FPDF_ANNOT */);
        r["render_ms"] = Ms(t);
        r["bitmap"] = $"{w}x{h} BGRA";
        r["nonwhite_px"] = CountNonWhite(buf, w * h);
        WriteBmp(Path.Combine(outDir, $"smoke-{Path.GetFileNameWithoutExtension(name)}.bmp"), buf, w, h);

        // --- same page through the matrix entry point (scale only, clip = whole bitmap)
        FPDFBitmap_FillRect(bmp, 0, 0, w, h, 0xFFFFFFFF);
        FsMatrix m = new() { A = scale, D = scale };
        FsRectF clip = new() { Left = 0, Top = 0, Right = w, Bottom = h };
        FPDF_RenderPageBitmapWithMatrix(bmp, page, &m, &clip, 0x01);
        r["matrix_render_nonwhite_px"] = CountNonWhite(buf, w * h);
        FPDFBitmap_Destroy(bmp);
        NativeMemory.Free(buf);

        // --- text
        t.Restart();
        nint tp = FPDFText_LoadPage(page);
        int chars = FPDFText_CountChars(tp);
        r["text_chars"] = chars;
        if (chars > 0)
        {
            ushort* tb = stackalloc ushort[65];
            int got = FPDFText_GetText(tp, 0, Math.Min(chars, 64), tb);
            r["text_head"] = new string((char*)tb, 0, Math.Max(0, got - 1)).Replace("\r", "\\r").Replace("\n", "\\n");
            double l, rr, b, tt;
            if (FPDFText_GetCharBox(tp, 0, &l, &rr, &b, &tt) != 0)
                r["char0_box_pt"] = $"l={l:F2} r={rr:F2} b={b:F2} t={tt:F2} (U+{FPDFText_GetUnicode(tp, 0):X4})";
        }
        r["text_ms"] = Ms(t);
        FPDFText_ClosePage(tp);
        FPDF_ClosePage(page);

        // --- bookmarks (top level), metadata, signatures
        var titles = new List<string>();
        for (nint bm = FPDFBookmark_GetFirstChild(doc, 0); bm != 0 && titles.Count < 20; bm = FPDFBookmark_GetNextSibling(doc, bm))
        {
            uint n = FPDFBookmark_GetTitle(bm, null, 0);
            if (n < 2) { titles.Add(""); continue; }
            ushort[] tb = new ushort[n / 2];
            fixed (ushort* p = tb) FPDFBookmark_GetTitle(bm, p, n);
            titles.Add(new string(MemoryMarshal.Cast<ushort, char>(tb)).TrimEnd('\0'));
        }
        r["top_level_bookmarks"] = titles;
        r["meta_producer"] = Meta(doc, "Producer");
        int sigs = FPDF_GetSignatureCount(doc);
        r["signatures"] = sigs;
        if (sigs > 0)
        {
            var subs = new List<string>();
            byte* sb = stackalloc byte[64];
            for (int i = 0; i < sigs; i++)
            {
                nint s = FPDF_GetSignatureObject(doc, i);
                uint n = FPDFSignatureObj_GetSubFilter(s, sb, 64);
                subs.Add(n > 1 ? Encoding.ASCII.GetString(sb, (int)n - 1) : "");
            }
            r["signature_subfilters"] = subs;
        }

        // --- save to memory (no file path): full copy and incremental, then reopen the output
        foreach ((string label, uint flag) in new[] { ("save_full", 2u /* FPDF_NO_INCREMENTAL */), ("save_incremental", 1u /* FPDF_INCREMENTAL */) })
        {
            byte[] saved = SaveToMemory(doc, flag, out int ok);
            using var re = new MemDoc(saved);
            nint doc2 = re.Open();
            int pages2 = doc2 != 0 ? FPDF_GetPageCount(doc2) : -1;
            if (doc2 != 0) FPDF_CloseDocument(doc2);
            r[label] = $"ok={ok} bytes={saved.Length} (src {bytes.Length}) reopen_pages={pages2}";
            if (ok == 0 || pages2 != pages) throw new InvalidOperationException($"{label} failed");
        }
    }
    finally { FPDF_CloseDocument(doc); }

    if ((int)r["nonwhite_px"]! == 0 && (int)r["text_chars"]! > 0 && (int)r["pages"]! > 0)
        throw new InvalidOperationException("page with text rendered fully white");
    Console.WriteLine($"  OK {name}: " + string.Join(", ", r.Where(kv => kv.Key is "pages" or "page0_size_pt" or "nonwhite_px" or "text_chars" or "text_head" or "signatures" or "load_ms" or "render_ms").Select(kv => $"{kv.Key}={kv.Value}")));
}

string Meta(nint doc, string tag)
{
    byte[] tg = Encoding.ASCII.GetBytes(tag + "\0");
    fixed (byte* tp = tg)
    {
        uint n = FPDF_GetMetaText(doc, tp, null, 0);
        if (n < 2) return "";
        ushort[] b = new ushort[n / 2];
        fixed (ushort* p = b) FPDF_GetMetaText(doc, tp, p, n);
        return new string(MemoryMarshal.Cast<ushort, char>(b)).TrimEnd('\0');
    }
}

static byte[] SaveToMemory(nint doc, uint flags, out int ok)
{
    var ms = new MemoryStream();
    WriteBlockFn cb = (self, data, size) => { ms.Write(new ReadOnlySpan<byte>(data, (int)size)); return 1; };
    FileWrite fw = new() { Version = 1, WriteBlock = Marshal.GetFunctionPointerForDelegate(cb) };
    ok = FPDF_SaveAsCopy(doc, &fw, flags);
    GC.KeepAlive(cb);
    return ms.ToArray();
}

static int CountNonWhite(byte* p, int px)
{
    int n = 0;
    for (int i = 0; i < px; i++) if (((uint*)p)[i] != 0xFFFFFFFFu) n++;
    return n;
}

static void WriteBmp(string path, byte* bgra, int w, int h)
{
    using var f = File.Create(path);
    using var bw = new BinaryWriter(f);
    int imgSize = w * h * 4;
    bw.Write((ushort)0x4D42); bw.Write(54 + imgSize); bw.Write(0); bw.Write(54);
    bw.Write(40); bw.Write(w); bw.Write(-h); bw.Write((ushort)1); bw.Write((ushort)32);
    bw.Write(0); bw.Write(imgSize); bw.Write(2835); bw.Write(2835); bw.Write(0); bw.Write(0);
    bw.Write(new ReadOnlySpan<byte>(bgra, imgSize));
}

static double Ms(Stopwatch t) => Math.Round(t.Elapsed.TotalMilliseconds, 2);

static string FindRepoRoot()
{
    for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        if (File.Exists(Path.Combine(d.FullName, "docs", "MASTER-PROMPT.md"))) return d.FullName;
    throw new DirectoryNotFoundException("repo root not found; pass --dll/--corpus/--out");
}

string? Arg(string key)
{
    string[] a = Environment.GetCommandLineArgs();
    int i = Array.IndexOf(a, key);
    return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
}
}
}

namespace Smoke
{
    /// <summary>A PDF held in a managed byte array, exposed to PDFium through FPDF_FILEACCESS.</summary>
    internal sealed unsafe class MemDoc : IDisposable
    {
        readonly byte[] _data;
        readonly GetBlockFn _cb;
        PdfFileAccess* _fa;

        public MemDoc(byte[] data)
        {
            _data = data;
            _cb = GetBlock;
            _fa = (PdfFileAccess*)NativeMemory.AllocZeroed((nuint)sizeof(PdfFileAccess));
            _fa->FileLen = (uint)data.Length;
            _fa->GetBlock = Marshal.GetFunctionPointerForDelegate(_cb);
        }

        int GetBlock(nint param, uint pos, byte* buf, uint size)
        {
            if ((ulong)pos + size > (ulong)_data.Length) return 0;
            new ReadOnlySpan<byte>(_data, (int)pos, (int)size).CopyTo(new Span<byte>(buf, (int)size));
            return 1;
        }

        public nint Open() => FPDF_LoadCustomDocument(_fa, null);

        public void Dispose()
        {
            if (_fa != null) { NativeMemory.Free(_fa); _fa = null; }
            GC.KeepAlive(_cb);
        }
    }
}
