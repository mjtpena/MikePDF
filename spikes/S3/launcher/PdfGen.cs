using System.Text;

namespace Quire.S3.Launcher;

/// <summary>Tiny hand-rolled PDF writer for fixtures (uncompressed, no xref streams). Fonts are referenced, never embedded.</summary>
public static class PdfGen
{
    public static byte[] Build(IReadOnlyList<string> pageContents, string fontResources, string extraObjects = "", int extraObjectStart = 100)
    {
        var objs = new SortedDictionary<int, string>();
        int n = pageContents.Count;
        // 1 catalog, 2 pages, 3 resources, pages at 10+2i (page) and 11+2i (content)
        objs[1] = "<< /Type /Catalog /Pages 2 0 R >>";
        var kids = string.Join(" ", Enumerable.Range(0, n).Select(i => $"{10 + 2 * i} 0 R"));
        objs[2] = $"<< /Type /Pages /Kids [{kids}] /Count {n} >>";
        objs[3] = $"<< /Font << {fontResources} >> >>";
        for (int i = 0; i < n; i++)
        {
            objs[10 + 2 * i] = $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources 3 0 R /Contents {11 + 2 * i} 0 R >>";
            objs[11 + 2 * i] = $"<< /Length {Encoding.Latin1.GetByteCount(pageContents[i])} >>\nstream\n{pageContents[i]}\nendstream";
        }
        if (extraObjects.Length > 0)
            foreach (var line in extraObjects.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                int sp = line.IndexOf(' ');
                objs[int.Parse(line[..sp])] = line[(sp + 1)..];
            }
        var sb = new StringBuilder("%PDF-1.7\n%âãÏÓ\n");
        var offsets = new Dictionary<int, int>();
        foreach (var (num, body) in objs) { offsets[num] = Encoding.Latin1.GetByteCount(sb.ToString()); sb.Append($"{num} 0 obj\n{body}\nendobj\n"); }
        int xref = Encoding.Latin1.GetByteCount(sb.ToString());
        int max = objs.Keys.Max();
        sb.Append($"xref\n0 {max + 1}\n0000000000 65535 f \n");
        for (int i = 1; i <= max; i++) sb.Append(offsets.TryGetValue(i, out var off) ? $"{off:D10} 00000 n \n" : "0000000000 65535 f \n");
        sb.Append($"trailer\n<< /Size {max + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    static string Esc(string s) => s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

    /// <summary>Page of text lines in many non-embedded fonts, including a Shift-JIS CJK line in MS Mincho.</summary>
    public static string BuildFontsFixture(string path)
    {
        var fonts = new (string res, string dict, string label)[]
        {
            ("F1", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>", "Helvetica (standard 14)"),
            ("F2", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>", "Helvetica-Bold (standard 14)"),
            ("F3", "<< /Type /Font /Subtype /Type1 /BaseFont /Times-Roman /Encoding /WinAnsiEncoding >>", "Times-Roman (standard 14)"),
            ("F4", "<< /Type /Font /Subtype /Type1 /BaseFont /Times-Italic /Encoding /WinAnsiEncoding >>", "Times-Italic (standard 14)"),
            ("F5", "<< /Type /Font /Subtype /Type1 /BaseFont /Courier /Encoding /WinAnsiEncoding >>", "Courier (standard 14)"),
            ("F6", "<< /Type /Font /Subtype /TrueType /BaseFont /Arial /Encoding /WinAnsiEncoding >>", "Arial (TrueType, not embedded)"),
            ("F7", "<< /Type /Font /Subtype /TrueType /BaseFont /Arial,BoldItalic /Encoding /WinAnsiEncoding >>", "Arial,BoldItalic (TrueType, not embedded)"),
            ("F8", "<< /Type /Font /Subtype /TrueType /BaseFont /TimesNewRomanPSMT /Encoding /WinAnsiEncoding >>", "TimesNewRomanPSMT (not embedded)"),
            ("F9", "<< /Type /Font /Subtype /TrueType /BaseFont /Georgia /Encoding /WinAnsiEncoding >>", "Georgia (not embedded, no std-14 equivalent)"),
            ("F10", "<< /Type /Font /Subtype /TrueType /BaseFont /SegoeUI /Encoding /WinAnsiEncoding >>", "SegoeUI (not embedded, no std-14 equivalent)"),
            ("F11", "<< /Type /Font /Subtype /TrueType /BaseFont /CourierNewPSMT /Encoding /WinAnsiEncoding >>", "CourierNewPSMT (not embedded)"),
            ("F12", "<< /Type /Font /Subtype /Type1 /BaseFont /Symbol >>", "Symbol (standard 14)"),
            ("F13", "<< /Type /Font /Subtype /TrueType /BaseFont /Verdana /Encoding /WinAnsiEncoding >>", "Verdana (not embedded)"),
        };
        var content = new StringBuilder();
        int y = 760;
        foreach (var (res, _, label) in fonts)
        {
            content.Append($"BT /{res} 13 Tf 36 {y} Td ({Esc(label)}: The quick brown fox 0123456789 fi fl) Tj ET\n");
            y -= 22;
        }
        // CJK, Type0 + CIDFontType2, non-embedded MS Mincho, 90ms-RKSJ-H, text bytes are Shift-JIS
        content.Append($"BT /F20 14 Tf 36 {y} Td <93FA967B8CEA82CC8365834C83588367> Tj ET\n");
        y -= 24;
        content.Append($"BT /F20 10 Tf 36 {y} Td <82A082A282A482A682A8> Tj ET\n");   // hiragana
        string fontRes = string.Join(" ", fonts.Select(f => $"/{f.res} {20 + Array.IndexOf(fonts, f)} 0 R")) + " /F20 40 0 R";
        var extra = new StringBuilder();
        for (int i = 0; i < fonts.Length; i++) extra.Append($"{20 + i} {fonts[i].dict}\n");
        extra.Append("40 << /Type /Font /Subtype /Type0 /BaseFont /MS-Mincho /Encoding /90ms-RKSJ-H /DescendantFonts [41 0 R] >>\n");
        extra.Append("41 << /Type /Font /Subtype /CIDFontType2 /BaseFont /MS-Mincho /CIDSystemInfo << /Registry (Adobe) /Ordering (Japan1) /Supplement 2 >> /DW 1000 /FontDescriptor 42 0 R >>\n");
        extra.Append("42 << /Type /FontDescriptor /FontName /MS-Mincho /Flags 6 /FontBBox [0 -137 1000 859] /ItalicAngle 0 /Ascent 859 /Descent -137 /CapHeight 700 /StemV 80 >>\n");
        File.WriteAllBytes(path, Build([content.ToString()], fontRes, extra.ToString()));
        return path;
    }

    /// <summary>20-page document: text + vector content (a stand-in for "a 20-page document" in the restart budget).</summary>
    public static string BuildTwentyPage(string path)
    {
        var pages = new List<string>();
        var rnd = new Random(1234);
        string[] words = "alpha beta gamma delta epsilon zeta eta theta iota kappa lambda mu nu xi omicron pi rho sigma tau upsilon phi chi psi omega quire reader engine tile sandbox".Split(' ');
        for (int p = 0; p < 20; p++)
        {
            var c = new StringBuilder();
            c.Append("0.9 0.9 1 rg 36 36 540 24 re f 0 g\n");
            c.Append($"BT /F1 18 Tf 36 750 Td (Page {p + 1} of 20) Tj ET\n");
            int y = 720;
            for (int l = 0; l < 45; l++, y -= 14)
            {
                var line = string.Join(" ", Enumerable.Range(0, 11).Select(_ => words[rnd.Next(words.Length)]));
                c.Append($"BT /F2 10 Tf 36 {y} Td ({line}) Tj ET\n");
            }
            for (int k = 0; k < 40; k++) c.Append($"{rnd.NextDouble():F2} {rnd.NextDouble():F2} {rnd.NextDouble():F2} rg {40 + rnd.Next(500)} {60 + rnd.Next(30)} {5 + rnd.Next(20)} {5 + rnd.Next(20)} re f\n");
            pages.Add(c.ToString());
        }
        File.WriteAllBytes(path, Build(pages, "/F1 100 0 R /F2 101 0 R", "100 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>\n101 << /Type /Font /Subtype /Type1 /BaseFont /Times-Roman >>\n"));
        return path;
    }

    /// <summary>One very dense page (small type, ~13k characters) for the GetCharBoxes serialisation measurement.</summary>
    public static string BuildDensePage(string path)
    {
        var rnd = new Random(77);
        string[] words = "the of and to in is that for it as was with be by on not he this are or his from at which but have an had they you were their one all we can her has there been if more when will would who so no".Split(' ');
        var c = new StringBuilder();
        int y = 780;
        while (y > 12) { var line = new StringBuilder(); while (line.Length < 125) line.Append(words[rnd.Next(words.Length)]).Append(' '); c.Append($"BT /F1 4.6 Tf 12 {y} Td ({line}) Tj ET\n"); y -= 5; }
        File.WriteAllBytes(path, Build([c.ToString()], "/F1 20 0 R", "20 << /Type /Font /Subtype /Type1 /BaseFont /Times-Roman >>\n"));
        return path;
    }
}
