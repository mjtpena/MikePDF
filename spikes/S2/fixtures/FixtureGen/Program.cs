using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static FixtureGen.Cs2;

namespace FixtureGen;

static class Cs2 { public static string F(double d) => PdfBuilder.F(d); }

static unsafe class Pdfium
{
    const string Dll = "pdfium.dll";
    [DllImport(Dll)] public static extern void FPDF_InitLibrary();
    [DllImport(Dll)] public static extern nint FPDF_LoadMemDocument(byte* data, int size, byte* pw);
    [DllImport(Dll)] public static extern nint FPDF_CreateNewDocument();
    [DllImport(Dll)] public static extern int FPDF_ImportPagesByIndex(nint dest, nint src, int* idx, uint len, int index);
    [DllImport(Dll)] public static extern int FPDF_GetPageCount(nint doc);
    [DllImport(Dll)] public static extern int FPDF_SaveAsCopy(nint doc, FileWrite* fw, uint flags);
    [DllImport(Dll)] public static extern void FPDF_CloseDocument(nint doc);
    [StructLayout(LayoutKind.Sequential)] public struct FileWrite { public int Version; public nint WriteBlock; }
    public delegate int WriteBlockFn(FileWrite* self, byte* data, uint size);
}

static class Program
{
    static readonly string[] Words = ("the of and to in is that for it as was with be by on not he this are or his from at which but have an had they you were their one all we can her has there been if more when will would who so no she other its may these than time into only do such could new two after first any then now people most over also made well between must before great back through where much should those same however another because year state during under work against both united every while since long might never city world still public form life without within many number upon system part general right house little course small place government order point case found given report act table section national following committee security attack agency information intelligence terrorist counter president director office staff review service official foreign military policy plan program reports effort operations needed need began later days between support american states department told said asked").Split(' ');

    static Random Rng(int seed) => new(seed);

    static string Sentence(Random r, int minW, int maxW)
    {
        int n = r.Next(minW, maxW + 1);
        var sb = new StringBuilder();
        for (int i = 0; i < n; i++)
        {
            string w = Words[r.Next(Words.Length)];
            if (i == 0) w = char.ToUpper(w[0]) + w[1..];
            sb.Append(w);
            if (i < n - 1) sb.Append(r.Next(18) == 0 ? ", " : " ");
        }
        sb.Append('.');
        return sb.ToString();
    }

    static List<string> WrapParagraph(string text, int maxChars)
    {
        var lines = new List<string>(); var cur = new StringBuilder();
        foreach (var w in text.Split(' '))
        {
            if (cur.Length + w.Length + 1 > maxChars) { lines.Add(cur.ToString().TrimEnd()); cur.Clear(); }
            cur.Append(w).Append(' ');
        }
        if (cur.Length > 0) lines.Add(cur.ToString().TrimEnd());
        return lines;
    }

    const string TextFonts = "<< /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Times-Roman /Encoding /WinAnsiEncoding >> /F2 << /Type /Font /Subtype /Type1 /BaseFont /Times-Bold /Encoding /WinAnsiEncoding >> /F3 << /Type /Font /Subtype /Type1 /BaseFont /Times-Italic /Encoding /WinAnsiEncoding >> /F4 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >> >> >>";

    /// <summary>Book/paper page: columns of Times 10-11 pt text, heading, header, page number.</summary>
    static byte[] TextPage(Random r, int pageNo, int columns, double w, double h, double size, double lead)
    {
        var c = new Cs();
        double mx = 72, my = 72, gap = 24, colW = (w - 2 * mx - (columns - 1) * gap) / columns;
        int maxChars = (int)(colW / (size * 0.43));
        c.Line($"BT /F4 8 Tf {F(mx)} {F(h - 40)} Td (QUIRE SPIKE S2 - SYNTHETIC TEXT FIXTURE) Tj ET");
        c.Line($"BT /F4 8 Tf {F(w / 2 - 10)} {F(40)} Td ({pageNo}) Tj ET");
        c.Line($"0.5 w {F(mx)} {F(h - 46)} m {F(w - mx)} {F(h - 46)} l S");
        double topY = h - my - 10;
        for (int col = 0; col < columns; col++)
        {
            double x = mx + col * (colW + gap), y = topY;
            if (col == 0 && pageNo % 2 == 1) { c.Line($"BT /F2 15 Tf {F(x)} {F(y)} Td ({Cs.Esc(Sentence(r, 3, 6).TrimEnd('.'))}) Tj ET"); y -= 28; }
            while (y > my + 10)
            {
                string para = string.Join(" ", Enumerable.Range(0, r.Next(3, 7)).Select(_ => Sentence(r, 8, 20)));
                bool first = true;
                foreach (var line in WrapParagraph(para, maxChars))
                {
                    if (y <= my + 10) break;
                    string font = r.Next(40) == 0 ? "F3" : "F1";
                    c.Line($"BT /{font} {F(size)} Tf {F(x + (first ? 14 : 0))} {F(y)} Td ({Cs.Esc(line)}) Tj ET");
                    first = false; y -= lead;
                }
                y -= lead * 0.5;
            }
        }
        return c.ToArray();
    }

    static byte[] BuildTextDoc(int pages, int columns, double w, double h, double size, double lead, int seed)
    {
        var r = Rng(seed); var pdf = new PdfBuilder();
        for (int i = 1; i <= pages; i++) pdf.AddPage(w, h, TextPage(r, i, columns, w, h, size, lead), TextFonts);
        return pdf.ToBytes();
    }

    static byte[] BuildVectorMap(int pages, int seed)
    {
        var r = Rng(seed); var pdf = new PdfBuilder();
        double w = 1224, h = 1584; // ANSI D landscape-ish points (17 x 22 in)
        for (int p = 0; p < pages; p++)
        {
            var c = new Cs();
            c.Line("0.93 0.95 0.9 rg 0 0 1224 1584 re f");
            // contour lines: random walks, ~420 contours x ~260 vertices
            for (int k = 0; k < 420; k++)
            {
                double x = r.NextDouble() * w, y = r.NextDouble() * h, a = r.NextDouble() * 6.28;
                double shade = 0.35 + 0.3 * r.NextDouble();
                c.Line($"{F(0.55 + shade * 0.2)} {F(0.35 + shade * 0.1)} {F(0.2)} RG {(k % 5 == 0 ? "0.9" : "0.35")} w");
                c.Raw($"{F(x)} {F(y)} m ");
                for (int i = 0; i < 260; i++) { a += (r.NextDouble() - 0.5) * 0.5; x += Math.Cos(a) * 5; y += Math.Sin(a) * 5; c.Raw($"{F(x)} {F(y)} l "); }
                c.Line(k % 7 == 0 ? "h S" : "S");
            }
            // road network + lakes
            for (int k = 0; k < 90; k++)
            {
                double x = r.NextDouble() * w, y = r.NextDouble() * h, a = r.NextDouble() * 6.28;
                c.Line("0.85 0.2 0.1 RG 2.2 w 1 J 1 j"); c.Raw($"{F(x)} {F(y)} m ");
                for (int i = 0; i < 120; i++) { a += (r.NextDouble() - 0.5) * 0.35; x += Math.Cos(a) * 8; y += Math.Sin(a) * 8; c.Raw($"{F(x)} {F(y)} l "); }
                c.Line("S 1 1 1 RG 0.8 w"); // cased roads
            }
            for (int k = 0; k < 60; k++)
            {
                double cx = r.NextDouble() * w, cy = r.NextDouble() * h;
                c.Line("0.6 0.8 0.95 rg 0.2 0.4 0.7 RG 0.6 w");
                c.Raw($"{F(cx)} {F(cy)} m ");
                for (int i = 0; i < 40; i++) { double a = i / 40.0 * 6.283; double rr = 10 + 6 * Math.Sin(a * 3 + k) + r.NextDouble() * 3; c.Raw($"{F(cx + Math.Cos(a) * rr * 2)} {F(cy + Math.Sin(a) * rr)} l "); }
                c.Line("h B");
            }
            c.Line("0 g");
            for (int k = 0; k < 500; k++) c.Line($"BT /F4 {F(5 + r.Next(5))} Tf {F(r.NextDouble() * (w - 60))} {F(r.NextDouble() * (h - 20))} Td ({Cs.Esc(Words[r.Next(Words.Length)])} {r.Next(100, 9999)}) Tj ET");
            pdf.AddPage(w, h, c.ToArray(), TextFonts);
        }
        return pdf.ToBytes();
    }

    static byte[] BuildTransparency(int pages, int seed)
    {
        var r = Rng(seed); var pdf = new PdfBuilder();
        // luminosity mask group: radial gradient white->black
        int shRadial = pdf.Add("<< /ShadingType 3 /ColorSpace /DeviceGray /Coords [306 396 0 306 396 300] /Function << /FunctionType 2 /Domain [0 1] /C0 [1] /C1 [0] /N 1 >> /Extend [true true] >>");
        int maskForm = pdf.AddStream($"/Type /XObject /Subtype /Form /BBox [0 0 612 792] /Group << /S /Transparency /CS /DeviceGray >> /Resources << /Shading << /S1 {shRadial} 0 R >> >>", new Cs().Line("/S1 sh").ToArray());
        int shAxial = pdf.Add("<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 612 792] /Function << /FunctionType 2 /Domain [0 1] /C0 [0.95 0.85 0.6] /C1 [0.4 0.55 0.9] /N 1 >> /Extend [true true] >>");
        for (int p = 0; p < pages; p++)
        {
            // group form XObject with 40 overlapping translucent discs, isolated + knockout variants alternate
            var g = new Cs();
            for (int i = 0; i < 40; i++)
            {
                g.Line($"{F(r.NextDouble())} {F(r.NextDouble())} {F(r.NextDouble())} rg");
                g.Circle(60 + r.NextDouble() * 490, 100 + r.NextDouble() * 590, 40 + r.NextDouble() * 90); g.Line("f");
            }
            int grp = pdf.AddStream($"/Type /XObject /Subtype /Form /BBox [0 0 612 792] /Group << /S /Transparency /I true /K {(p % 2 == 0 ? "false" : "true")} /CS /DeviceRGB >> /Resources << /ExtGState << /A << /ca 0.45 /CA 0.45 >> >> >>", new Cs().Line("/A gs").Bytes(g.ToArray()).ToArray());
            var c = new Cs();
            c.Line("q /Sh1 sh Q");
            c.Line("q /G1 gs /Grp Do Q");                                   // group painted with alpha
            c.Line("q /Mul gs 0.9 0.4 0.1 rg"); for (int i = 0; i < 25; i++) { c.Circle(r.NextDouble() * 612, r.NextDouble() * 792, 30 + r.NextDouble() * 70); c.Line("f"); } c.Line("Q");
            c.Line("q /Scr gs 0.2 0.5 0.9 rg"); for (int i = 0; i < 25; i++) { c.Circle(r.NextDouble() * 612, r.NextDouble() * 792, 30 + r.NextDouble() * 70); c.Line("f"); } c.Line("Q");
            c.Line("q /SM gs 0.1 0.1 0.1 rg 0 0 612 792 re f Q");           // soft-masked fill (luminosity radial)
            c.Line("q /G1 gs BT /F4 36 Tf 60 700 Td (Transparency groups) Tj ET Q");
            string res = $"<< /Font << /F4 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> /Shading << /Sh1 {shAxial} 0 R >> /XObject << /Grp {grp} 0 R >> /ExtGState << /G1 << /ca 0.6 /CA 0.6 >> /Mul << /BM /Multiply /ca 0.7 >> /Scr << /BM /Screen /ca 0.7 >> /SM << /SMask << /Type /Mask /S /Luminosity /G {maskForm} 0 R >> >> >> >>";
            pdf.AddPage(612, 792, c.ToArray(), res);
        }
        return pdf.ToBytes();
    }

    static byte[] BuildHugePage(int seed)
    {
        var r = Rng(seed); var pdf = new PdfBuilder();
        double s = 14400; // 200 in, the PDF-spec (1.7) maximum user-space extent
        var c = new Cs();
        c.Line("0.97 0.97 0.9 rg 0 0 14400 14400 re f");
        c.Line("0.7 w 0.6 0.6 0.7 RG");
        for (int i = 0; i <= 14400; i += 72) c.Line($"{i} 0 m {i} {F(s)} l S 0 {i} m {F(s)} {i} l S");
        for (int k = 0; k < 6000; k++)
        {
            c.Line($"{F(r.NextDouble() * 0.8)} {F(r.NextDouble() * 0.8)} {F(r.NextDouble() * 0.8)} rg");
            double x = r.NextDouble() * (s - 400), y = r.NextDouble() * (s - 400), sz = 20 + r.NextDouble() * 160;
            switch (k % 3) { case 0: c.Line($"{F(x)} {F(y)} {F(sz)} {F(sz * 0.6)} re f"); break; case 1: c.Circle(x, y, sz * 0.5); c.Line("f"); break; default: c.Line($"{F(x)} {F(y)} m {F(x + sz)} {F(y + sz * 0.3)} l {F(x + sz * 0.4)} {F(y + sz)} l h f"); break; }
        }
        c.Line("0 g");
        for (int k = 0; k < 3000; k++) c.Line($"BT /F1 {F(14 + r.Next(60))} Tf {F(r.NextDouble() * (s - 800))} {F(r.NextDouble() * (s - 100))} Td ({Cs.Esc(Sentence(r, 3, 8))}) Tj ET");
        pdf.AddPage(s, s, c.ToArray(), TextFonts);
        return pdf.ToBytes();
    }

    static byte[] BuildType3(int pages, int seed)
    {
        var r = Rng(seed); var pdf = new PdfBuilder();
        int glyphs = 64;
        var procs = new int[glyphs];
        for (int g = 0; g < glyphs; g++)
        {
            var cs = new Cs();
            cs.Line("600 0 0 0 560 700 d1");
            if (g % 4 == 3)
            {   // bitmap glyph: 1-bit image mask 16x16
                var bits = new byte[32]; r.NextBytes(bits);
                cs.Line("q 560 0 0 700 0 0 cm").Raw("BI /IM true /W 16 /H 16 /BPC 1 /D [1 0] ID ").Bytes(bits).Line("\nEI Q");
            }
            else
            {
                cs.Raw($"{r.Next(20, 120)} 0 m ");
                int n = r.Next(5, 11);
                for (int i = 0; i < n; i++) { if (i % 3 == 2) cs.Raw($"{r.Next(40, 540)} {r.Next(0, 700)} {r.Next(40, 540)} {r.Next(0, 700)} {r.Next(40, 540)} {r.Next(0, 700)} c "); else cs.Raw($"{r.Next(40, 540)} {r.Next(0, 700)} l "); }
                cs.Line("h f");
            }
            procs[g] = pdf.AddStream("", cs.ToArray(), flate: false);
        }
        var diffs = string.Join(" ", Enumerable.Range(0, glyphs).Select(g => $"/g{g}"));
        var charProcs = string.Join(" ", Enumerable.Range(0, glyphs).Select(g => $"/g{g} {procs[g]} 0 R"));
        int font = pdf.Add($"<< /Type /Font /Subtype /Type3 /Name /T3 /FontBBox [0 0 600 700] /FontMatrix [0.001 0 0 0.001 0 0] /CharProcs << {charProcs} >> /Encoding << /Type /Encoding /Differences [32 {diffs}] >> /FirstChar 32 /LastChar {32 + glyphs - 1} /Widths [{string.Join(" ", Enumerable.Repeat("600", glyphs))}] /Resources << >> >>");
        for (int p = 0; p < pages; p++)
        {
            var c = new Cs();
            for (int line = 0; line < 52; line++)
            {
                var hex = new StringBuilder();
                for (int i = 0; i < 80; i++) hex.Append((32 + r.Next(glyphs)).ToString("X2"));
                c.Line($"BT /T3 10 Tf 60 {F(740 - line * 13.5)} Td <{hex}> Tj ET");
            }
            pdf.AddPage(612, 792, c.ToArray(), $"<< /Font << /T3 {font} 0 R >> >>");
        }
        return pdf.ToBytes();
    }

    const string Hanzi = "的一是不了人我在有他这为之大来以个中上们到说国和地也子时道出而要于就下得可你年生自会那后能对着事其里所去行过家十用发天如然作方成者多日都三小军二无同么经法当起与好看学进种将还分此心前面又定见只主没公从信法管理政府文件阅读器渲染性能测试页面缓存图块显示";

    static byte[] BuildCjk(int pages, int seed)
    {
        var r = Rng(seed); var pdf = new PdfBuilder();
        int cid = pdf.Add("<< /Type /Font /Subtype /CIDFontType0 /BaseFont /STSong-Light /CIDSystemInfo << /Registry (Adobe) /Ordering (GB1) /Supplement 2 >> /DW 1000 >>");
        int font = pdf.Add($"<< /Type /Font /Subtype /Type0 /BaseFont /STSong-Light /Encoding /UniGB-UCS2-H /DescendantFonts [{cid} 0 R] >>");
        for (int p = 0; p < pages; p++)
        {
            var c = new Cs();
            for (int line = 0; line < 44; line++)
            {
                var hex = new StringBuilder();
                for (int i = 0; i < 38; i++) hex.Append(((int)Hanzi[r.Next(Hanzi.Length)]).ToString("X4"));
                c.Line($"BT /C1 12 Tf 72 {F(730 - line * 15.5)} Td <{hex}> Tj ET");
            }
            pdf.AddPage(612, 792, c.ToArray(), $"<< /Font << /C1 {font} 0 R >> >>");
        }
        return pdf.ToBytes();
    }

    static byte[] BuildJpx(string jp2Path, int w, int h, int pages)
    {
        var pdf = new PdfBuilder();
        var data = File.ReadAllBytes(jp2Path);
        int img = pdf.AddStream($"/Type /XObject /Subtype /Image /Width {w} /Height {h} /Filter /JPXDecode", data, flate: false);
        // /Filter given in dict above; AddStream with flate:false adds no second filter
        for (int p = 0; p < pages; p++)
        {
            var c = new Cs().Line($"q 612 0 0 612 0 90 cm /Im Do Q BT /F4 12 Tf 72 60 Td (JPX photo fixture page {p + 1}) Tj ET");
            pdf.AddPage(612, 792, c.ToArray(), $"<< /XObject << /Im {img} 0 R >> /Font << /F4 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >>");
        }
        return pdf.ToBytes();
    }

    /// <summary>Import a source document's pages repeatedly with FPDF_ImportPagesByIndex until <paramref name="total"/> pages exist.</summary>
    static unsafe byte[] BuildByImport(byte[] src, int total)
    {
        Pdfium.FPDF_InitLibrary();
        nint sdoc;
        fixed (byte* p = src) sdoc = Pdfium.FPDF_LoadMemDocument(p, src.Length, null);
        if (sdoc == 0) throw new Exception("load src failed");
        int n = Pdfium.FPDF_GetPageCount(sdoc);
        nint dest = Pdfium.FPDF_CreateNewDocument();
        var idx = Enumerable.Range(0, n).ToArray();
        int have = 0;
        while (have < total)
        {
            int take = Math.Min(n, total - have);
            fixed (int* ip = idx) if (Pdfium.FPDF_ImportPagesByIndex(dest, sdoc, ip, (uint)take, have) == 0) throw new Exception("import failed at " + have);
            have += take;
        }
        var ms = new MemoryStream();
        var gch = GCHandle.Alloc(ms);
        Pdfium.WriteBlockFn cb = (self, data, size) => { ms.Write(new ReadOnlySpan<byte>(data, (int)size)); return 1; };
        var fw = new Pdfium.FileWrite { Version = 1, WriteBlock = Marshal.GetFunctionPointerForDelegate(cb) };
        int ok = Pdfium.FPDF_SaveAsCopy(dest, &fw, 2 /*FPDF_NO_INCREMENTAL*/);
        GC.KeepAlive(cb); gch.Free();
        Pdfium.FPDF_CloseDocument(dest); Pdfium.FPDF_CloseDocument(sdoc);
        if (ok == 0) throw new Exception("save failed");
        return ms.ToArray();
    }

    static void Main(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : "fixtures";
        string dl = args.Length > 1 ? args[1] : "";
        Directory.CreateDirectory(outDir);
        var manifest = new List<object>();
        void Save(string name, byte[] data, string kind, string source)
        {
            string path = Path.Combine(outDir, name);
            File.WriteAllBytes(path, data);
            string sha = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
            Console.WriteLine($"{name,-22} {data.Length / 1024.0,10:F0} KB  {sha[..16]}  {kind}");
            manifest.Add(new { name, bytes = data.Length, sha256 = sha, kind, source });
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var book = BuildTextDoc(40, 1, 612, 792, 11, 13.6, 1001);
        Save("text-book.pdf", book, "1 text-heavy book page (single column, Times 11/13.6, 40 pages)", "generated (FixtureGen, seed 1001)");
        Save("text-20.pdf", BuildTextDoc(20, 1, 612, 792, 11, 13.6, 1002), "typical 20-page text PDF (TTFT fixture)", "generated (seed 1002)");
        Save("two-column.pdf", BuildTextDoc(30, 2, 612, 792, 9, 11.2, 1003), "2 two-column paper (Times 9/11.2, 30 pages)", "generated (seed 1003)");
        Save("vector-map.pdf", BuildVectorMap(3, 1004), "3 dense vector map, 17x22 in, 3 pages, ~110k path segments each", "generated (seed 1004)");
        Save("transparency.pdf", BuildTransparency(12, 1005), "6 transparency groups, blend modes, luminosity soft mask, shading", "generated (seed 1005)");
        Save("huge-page.pdf", BuildHugePage(1006), "7 huge page 200x200 in (14400 x 14400 pt), 9000 objects", "generated (seed 1006)");
        Save("type3.pdf", BuildType3(24, 1007), "8 Type 3 fonts (vector + image-mask glyphs), 24 pages", "generated (seed 1007)");
        Save("cjk.pdf", BuildCjk(24, 1008), "9 CJK text (STSong-Light / UniGB-UCS2-H, not embedded), 24 pages", "generated (seed 1008)");
        Save("text-2000.pdf", BuildByImport(book, 2000), "10 2,000-page text document via FPDF_ImportPagesByIndex of text-book.pdf x50", "generated (FPDF_ImportPagesByIndex)");
        // stress documents for the fling: heavy pages, many of them (import of the heavy fixtures)
        Save("stress-vector-60.pdf", BuildByImport(File.ReadAllBytes(Path.Combine(outDir, "vector-map.pdf")), 60), "stress: 60 dense vector-map pages (17x22 in), via FPDF_ImportPagesByIndex", "generated (import of vector-map.pdf x20)");
        Save("stress-transp-60.pdf", BuildByImport(File.ReadAllBytes(Path.Combine(outDir, "transparency.pdf")), 60), "stress: 60 transparency-group pages, via FPDF_ImportPagesByIndex", "generated (import of transparency.pdf x5)");
        if (dl != "" && File.Exists(Path.Combine(dl, "photo.jp2")))
        {
            var dim = File.ReadAllText(Path.Combine(dl, "photo.dim")).Split('x');
            Save("jpx-photo.pdf", BuildJpx(Path.Combine(dl, "photo.jp2"), int.Parse(dim[0]), int.Parse(dim[1]), 4), "5 JPX photo (JPEG2000, NASA/JPL PIA18033 Blue Marble, re-encoded), 4 pages", "derived from nasa-img/PIA18033~orig.jpg");
        }
        File.WriteAllText(Path.Combine(outDir, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"done in {sw.Elapsed.TotalSeconds:F1} s");
    }
}
