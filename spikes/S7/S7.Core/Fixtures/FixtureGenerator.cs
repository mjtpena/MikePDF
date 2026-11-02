using System.Text;

namespace S7.Core.Fixtures;

/// <summary>
/// Writes the four S7 fixture PDFs by hand (no PDF library; PDFium's edit API cannot author a
/// structure tree or MCIDs). The PDFium test corpus has tagged_* files but they are tiny
/// ("Hello, world!" style), have no multi-line paragraphs, and none has a StructTreeRoot whose order
/// differs from content-stream order, so they cannot answer the reading-order question.
/// </summary>
public static class FixtureGenerator
{
    public const string TaggedName = "a-tagged-single-column.pdf";
    public const string TwoColName = "b-untagged-two-column.pdf";
    public const string RotateName = "c-rotate90-cropbox.pdf";
    public const string LigatureName = "d-ligatures-hyphenation.pdf";

    public static void WriteAll(string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, TaggedName), Tagged());
        File.WriteAllBytes(Path.Combine(dir, TwoColName), TwoColumn());
        File.WriteAllBytes(Path.Combine(dir, RotateName), RotateCrop());
        File.WriteAllBytes(Path.Combine(dir, LigatureName), LigaturesHyphenation());
    }

    // ------------------------------------------------------------------------------------------
    // (a) Tagged single column. Content-stream order is deliberately NOT reading order:
    //     footer artifact, paragraph 2, heading, paragraph 1. The structure tree says H1, P, P.
    public static byte[] Tagged()
    {
        var content = new StringBuilder();
        content.Append("/Artifact BMC BT /F1 10 Tf 1 0 0 1 270 40 Tm (Page 1 of 1) Tj ET EMC\n");
        content.Append("/P << /MCID 2 >> BDC BT /F1 12 Tf 1 0 0 1 72 560 Tm 16 TL (Funding was released in May. Delivery is) Tj T* (expected before the end of the year.) Tj ET EMC\n");
        content.Append("/H1 << /MCID 0 >> BDC BT /F2 22 Tf 1 0 0 1 72 700 Tm (Quarterly Findings) Tj ET EMC\n");
        content.Append("/P << /MCID 1 >> BDC BT /F1 12 Tf 1 0 0 1 72 640 Tm 16 TL (The committee reviewed three proposals during) Tj T* (the spring session and approved two of them.) Tj ET EMC\n");

        var b = new PdfBuilder();
        b.Add(1, "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R /MarkInfo << /Marked true >> /Lang (en-US) >>");
        b.Add(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        b.Add(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R /F2 8 0 R >> >> /StructParents 0 /Tabs /S >>");
        b.AddStream(4, "", content.ToString());
        b.Add(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Times-Roman /Encoding /WinAnsiEncoding >>");
        b.Add(8, "<< /Type /Font /Subtype /Type1 /BaseFont /Times-Bold /Encoding /WinAnsiEncoding >>");
        b.Add(6, "<< /Type /StructTreeRoot /K [9 0 R] /ParentTree 7 0 R >>");
        b.Add(7, "<< /Nums [0 [10 0 R 11 0 R 12 0 R]] >>");
        b.Add(9, "<< /Type /StructElem /S /Document /P 6 0 R /K [10 0 R 11 0 R 12 0 R] >>");
        b.Add(10, "<< /Type /StructElem /S /H1 /P 9 0 R /Pg 3 0 R /K 0 >>");
        b.Add(11, "<< /Type /StructElem /S /P /P 9 0 R /Pg 3 0 R /K 1 >>");
        b.Add(12, "<< /Type /StructElem /S /P /P 9 0 R /Pg 3 0 R /K 2 >>");
        return b.Build();
    }

    // ------------------------------------------------------------------------------------------
    // (b) Untagged two column page. Title spans both columns. Lines are emitted row by row
    //     (L1, R1, L2, R2, ...) as some producers do, so content-stream order is wrong.
    public static readonly string[] TwoColLeft =
    {
        "Reading order is easy when a page has one",
        "column, because the text simply runs from",
        "the top of the page to the bottom. Two",
        "columns change that, since the eye jumps to",
        "the top of the second column after finishing",
        "the first one.",
        "",
        "A screen reader must make the same jump.",
        "Without help, it reads straight across the",
        "page and mixes the two columns together.",
    };
    public static readonly string[] TwoColRight =
    {
        "The heuristic used here cuts the page into",
        "regions wherever it finds a clear empty band.",
        "A title that spans both columns is read",
        "first, then the left column, then the right.",
        "Paragraphs are separated by extra vertical",
        "space between two lines.",
        "",
        "Narrow gaps inside a line, such as the space",
        "between two words, never cause a cut.",
        "Only gutters of a full em or more count.",
    };

    public static byte[] TwoColumn()
    {
        var c = new StringBuilder();
        c.Append("BT /F2 18 Tf 1 0 0 1 72 730 Tm (A Study of Reading Order in Two Column Pages) Tj ET\n");
        double y = 690;
        for (int i = 0; i < TwoColLeft.Length; i++, y -= 12)
        {
            if (TwoColLeft[i].Length > 0)
                c.Append($"BT /F1 10 Tf 1 0 0 1 72 {y} Tm ({TwoColLeft[i]}) Tj ET\n");
            if (TwoColRight[i].Length > 0)
                c.Append($"BT /F1 10 Tf 1 0 0 1 330 {y} Tm ({TwoColRight[i]}) Tj ET\n");
        }
        var b = new PdfBuilder();
        b.Add(1, "<< /Type /Catalog /Pages 2 0 R >>");
        b.Add(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        b.Add(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R /F2 6 0 R >> >> >>");
        b.AddStream(4, "", c.ToString());
        b.Add(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Times-Roman /Encoding /WinAnsiEncoding >>");
        b.Add(6, "<< /Type /Font /Subtype /Type1 /BaseFont /Times-Bold /Encoding /WinAnsiEncoding >>");
        return b.Build();
    }

    // ------------------------------------------------------------------------------------------
    // (c) /Rotate 90 and a CropBox whose origin is (100,150). One line sits outside the CropBox.
    public static byte[] RotateCrop()
    {
        // Page space (what the user sees at /Rotate 90, view rotation 0) is upright text. The page is 500 wide x 400 tall
        // (CropBox 400x500 rotated). Display (X right, Y down) maps to user space as x = 100 + Y, y = 150 + X, so text runs
        // up the user-space y axis: Tm = [0 1 -1 0 x y].
        var c = new StringBuilder();
        c.Append("BT /F1 14 Tf 0 1 -1 0 118 154 Tm (TOP LEFT) Tj ET\n");
        c.Append("BT /F1 14 Tf 0 1 -1 0 490 450 Tm (BOTTOM RIGHT) Tj ET\n");
        string[] lines =
        {
            "Rotated page with an offset crop box.",
            "The page dictionary carries Rotate 90 and",
            "a CropBox whose origin is not at zero.",
            "Reading it correctly needs the transform.",
        };
        for (int i = 0; i < lines.Length; i++)
            c.Append($"BT /F1 12 Tf 0 1 -1 0 {200 + 20 * i} 170 Tm ({lines[i]}) Tj ET\n");
        // Outside the CropBox (user x < 100): must not appear in the accessible text.
        c.Append("BT /F1 12 Tf 0 1 -1 0 60 170 Tm (Hidden line outside the crop box.) Tj ET\n");
        var b = new PdfBuilder();
        b.Add(1, "<< /Type /Catalog /Pages 2 0 R >>");
        b.Add(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        b.Add(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /CropBox [100 150 500 650] /Rotate 90 /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>");
        b.AddStream(4, "", c.ToString());
        b.Add(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        return b.Build();
    }

    // ------------------------------------------------------------------------------------------
    // (d) Ligatures and hyphenation. Font has no /Encoding so Standard Encoding applies, where
    //     code 0xAE (octal 256) is the glyph "fi" and 0xAF (octal 257) is "fl".
    public static byte[] LigaturesHyphenation()
    {
        var c = new StringBuilder();
        c.Append("BT /F1 14 Tf 1 0 0 1 72 700 Tm 22 TL ");
        c.Append("(The of\\256cial of\\257ine work\\257ow is a well-known) Tj T* ");
        c.Append("(case of \\256nding \\257aws in the data.) Tj ET\n");
        c.Append("BT /F1 14 Tf 1 0 0 1 72 640 Tm 22 TL ");
        c.Append("(This sentence is a hyphenated exam-) Tj T* ");
        c.Append("(ple of line breaking, and so is a sec-) Tj T* ");
        c.Append("(ond one that wraps again.) Tj ET\n");
        var b = new PdfBuilder();
        b.Add(1, "<< /Type /Catalog /Pages 2 0 R >>");
        b.Add(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        b.Add(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>");
        b.AddStream(4, "", c.ToString());
        b.Add(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Times-Roman >>");
        return b.Build();
    }

    private sealed class PdfBuilder
    {
        private readonly SortedDictionary<int, byte[]> _objs = new();
        private static readonly Encoding Latin1 = Encoding.Latin1;

        public void Add(int id, string body) => _objs[id] = Latin1.GetBytes($"{id} 0 obj\n{body}\nendobj\n");

        public void AddStream(int id, string dictExtra, string data)
        {
            var bytes = Latin1.GetBytes(data);
            var head = Latin1.GetBytes($"{id} 0 obj\n<< /Length {bytes.Length} {dictExtra} >>\nstream\n");
            var tail = Latin1.GetBytes("\nendstream\nendobj\n");
            _objs[id] = head.Concat(bytes).Concat(tail).ToArray();
        }

        public byte[] Build()
        {
            using var ms = new MemoryStream();
            void W(string s) { var b = Latin1.GetBytes(s); ms.Write(b, 0, b.Length); }
            W("%PDF-1.7\n%âãÏÓ\n");
            int max = _objs.Keys.Max();
            var offsets = new long[max + 1];
            foreach (var (id, bytes) in _objs)
            {
                offsets[id] = ms.Position;
                ms.Write(bytes, 0, bytes.Length);
            }
            long xref = ms.Position;
            W($"xref\n0 {max + 1}\n");
            W("0000000000 65535 f \n");
            for (int i = 1; i <= max; i++)
                W(_objs.ContainsKey(i) ? $"{offsets[i]:D10} 00000 n \n" : "0000000000 65535 f \n");
            W($"trailer\n<< /Size {max + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            return ms.ToArray();
        }
    }
}
