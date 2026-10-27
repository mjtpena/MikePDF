// S8.1: signature enumeration through the PDFium C API (fpdf_signature.h, "Experimental API", read-only).
// Usage: S8.Enum <file-or-dir>... [--out <dir>] [--only-signed]
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace S8.Enum;

static unsafe class Pdfium
{
    const string Dll = "pdfium.dll";

    [StructLayout(LayoutKind.Sequential)]
    public struct LibraryConfig
    {
        public int version;          // must be 2
        public IntPtr userFontPaths; // const char**
        public IntPtr isolate;
        public uint v8EmbedderSlot;
        public IntPtr platform;
    }

    [DllImport(Dll)] public static extern void FPDF_InitLibraryWithConfig(ref LibraryConfig cfg);
    [DllImport(Dll)] public static extern void FPDF_DestroyLibrary();
    [DllImport(Dll)] public static extern IntPtr FPDF_LoadMemDocument64(byte* data, nuint size, byte* password);
    [DllImport(Dll)] public static extern void FPDF_CloseDocument(IntPtr doc);
    [DllImport(Dll)] public static extern uint FPDF_GetLastError();
    [DllImport(Dll)] public static extern int FPDF_GetPageCount(IntPtr doc);

    // fpdf_signature.h  (unsigned long == 32 bit on Windows)
    [DllImport(Dll)] public static extern int FPDF_GetSignatureCount(IntPtr doc);
    [DllImport(Dll)] public static extern IntPtr FPDF_GetSignatureObject(IntPtr doc, int index);
    [DllImport(Dll)] public static extern uint FPDFSignatureObj_GetContents(IntPtr sig, byte* buffer, uint length);
    [DllImport(Dll)] public static extern uint FPDFSignatureObj_GetByteRange(IntPtr sig, int* buffer, uint length);
    [DllImport(Dll)] public static extern uint FPDFSignatureObj_GetSubFilter(IntPtr sig, byte* buffer, uint length);
    [DllImport(Dll)] public static extern uint FPDFSignatureObj_GetReason(IntPtr sig, byte* buffer, uint length);
    [DllImport(Dll)] public static extern uint FPDFSignatureObj_GetTime(IntPtr sig, byte* buffer, uint length);
    [DllImport(Dll)] public static extern uint FPDFSignatureObj_GetDocMDPPermission(IntPtr sig);
}

record SigInfo(int Index, int ContentsLength, string ContentsHexPrefix, string ContentsSha256, int[] ByteRange, string? SubFilter,
               string? Reason, string? Time, uint DocMdp, bool ByteRangeCallReturnedZero, bool SubFilterCallReturnedZero);

record FileInfo(string File, long Size, string? LoadError, int Pages, int SignatureCount, List<SigInfo> Signatures, double LoadMs);

static class Program
{
    static readonly string[] ErrNames = { "SUCCESS", "UNKNOWN", "FILE", "FORMAT", "PASSWORD", "SECURITY", "PAGE" };

    static unsafe int Main(string[] args)
    {
        string outDir = Path.Combine(AppContext.BaseDirectory, "enum-out");
        bool onlySigned = false;
        var inputs = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--out") outDir = args[++i];
            else if (args[i] == "--only-signed") onlySigned = true;
            else inputs.Add(args[i]);
        }
        var files = new List<string>();
        foreach (var inp in inputs)
        {
            if (Directory.Exists(inp)) files.AddRange(Directory.EnumerateFiles(inp, "*.pdf", SearchOption.AllDirectories));
            else files.Add(inp);
        }
        files.Sort(StringComparer.OrdinalIgnoreCase);
        Directory.CreateDirectory(outDir);

        var cfg = new Pdfium.LibraryConfig { version = 2 };
        Pdfium.FPDF_InitLibraryWithConfig(ref cfg);
        var results = new List<FileInfo>();
        foreach (var f in files)
        {
            var fi = Enumerate(f);
            if (onlySigned && fi.SignatureCount <= 0) { results.Add(fi with { Signatures = new() }); continue; }
            results.Add(fi);
        }
        Pdfium.FPDF_DestroyLibrary();

        var opts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(outDir, "enum.json"), JsonSerializer.Serialize(results, opts));
        int withSig = results.Count(r => r.SignatureCount > 0), failed = results.Count(r => r.LoadError != null);
        Console.WriteLine($"files scanned: {results.Count}, load failures (expected for fuzz/bad corpus): {failed}, files with >=1 signature per PDFium: {withSig}");
        foreach (var r in results.Where(r => r.SignatureCount > 0))
        {
            Console.WriteLine($"\n{r.File}  ({r.Size} bytes, {r.Pages} page(s), load {r.LoadMs:F2} ms)  signatures={r.SignatureCount}");
            foreach (var s in r.Signatures)
                Console.WriteLine($"  [{s.Index}] subFilter={s.SubFilter ?? "(none)"} byteRange=[{string.Join(' ', s.ByteRange)}] contentsLen={s.ContentsLength} docMDP={s.DocMdp} time={s.Time ?? "(none)"} reason={s.Reason ?? "(none)"}");
        }
        return 0;
    }

    static unsafe FileInfo Enumerate(string path)
    {
        var data = File.ReadAllBytes(path);
        var sw = Stopwatch.StartNew();
        IntPtr doc;
        fixed (byte* p = data) { doc = Pdfium.FPDF_LoadMemDocument64(p, (nuint)data.Length, null); }
        if (doc == IntPtr.Zero)
        {
            uint e = Pdfium.FPDF_GetLastError();
            return new FileInfo(path, data.Length, e < ErrNames.Length ? ErrNames[e] : $"err{e}", 0, -1, new(), sw.Elapsed.TotalMilliseconds);
        }
        // NOTE: `data` must stay alive while `doc` is open (FPDF_LoadMemDocument does not copy). GC.KeepAlive below.
        double loadMs = sw.Elapsed.TotalMilliseconds;
        int pages = Pdfium.FPDF_GetPageCount(doc);
        int count = Pdfium.FPDF_GetSignatureCount(doc);
        var list = new List<SigInfo>();
        for (int i = 0; i < count; i++)
        {
            var sig = Pdfium.FPDF_GetSignatureObject(doc, i);
            if (sig == IntPtr.Zero) continue;
            // contents
            uint clen = Pdfium.FPDFSignatureObj_GetContents(sig, null, 0);
            var cbuf = new byte[clen];
            if (clen > 0) fixed (byte* cp = cbuf) Pdfium.FPDFSignatureObj_GetContents(sig, cp, clen);
            // byte range (length is in ints)
            uint brn = Pdfium.FPDFSignatureObj_GetByteRange(sig, null, 0);
            var br = new int[brn];
            if (brn > 0) fixed (int* bp = br) Pdfium.FPDFSignatureObj_GetByteRange(sig, bp, brn);
            // subfilter
            uint sfl = Pdfium.FPDFSignatureObj_GetSubFilter(sig, null, 0);
            string? sf = null;
            if (sfl > 0)
            {
                var sb = new byte[sfl];
                fixed (byte* sp = sb) Pdfium.FPDFSignatureObj_GetSubFilter(sig, sp, sfl);
                sf = Encoding.ASCII.GetString(sb, 0, (int)sfl - 1);
            }
            // reason (UTF-16LE)
            uint rl = Pdfium.FPDFSignatureObj_GetReason(sig, null, 0);
            string? reason = null;
            if (rl > 2)
            {
                var rb = new byte[rl];
                fixed (byte* rp = rb) Pdfium.FPDFSignatureObj_GetReason(sig, rp, rl);
                reason = Encoding.Unicode.GetString(rb, 0, (int)rl - 2);
            }
            uint tl = Pdfium.FPDFSignatureObj_GetTime(sig, null, 0);
            string? time = null;
            if (tl > 1)
            {
                var tb = new byte[tl];
                fixed (byte* tp = tb) Pdfium.FPDFSignatureObj_GetTime(sig, tp, tl);
                time = Encoding.ASCII.GetString(tb, 0, (int)tl - 1);
            }
            uint mdp = Pdfium.FPDFSignatureObj_GetDocMDPPermission(sig);
            string prefix = Convert.ToHexString(cbuf.AsSpan(0, Math.Min(8, cbuf.Length)));
            string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(cbuf));
            list.Add(new SigInfo(i, cbuf.Length, prefix, sha, br, sf, reason, time, mdp, brn == 0, sfl == 0));
        }
        Pdfium.FPDF_CloseDocument(doc);
        GC.KeepAlive(data);
        return new FileInfo(path, data.Length, null, pages, count, list, loadMs);
    }
}
