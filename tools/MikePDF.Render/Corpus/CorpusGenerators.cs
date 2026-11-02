using System.Globalization;
using System.Text;

namespace MikePDF.Render.Corpus;

/// <summary>
/// The corpus files MikePDF makes itself (ADR-0010: generated files belong to the project, so their licence is Apache-2.0 and they are never committed;
/// the manifest records their SHA-256 and this class must reproduce it exactly). Each one aims at a rule of the renderer: rotation, CropBox origins,
/// page counts, structure damage, extreme sizes, deep nesting, transparency.
/// </summary>
internal static class CorpusGenerators
{
    private static readonly string[] StandardFonts =
    [
        "Helvetica", "Helvetica-Bold", "Helvetica-Oblique", "Helvetica-BoldOblique", "Times-Roman", "Times-Bold", "Times-Italic",
        "Times-BoldItalic", "Courier", "Courier-Bold", "Courier-Oblique", "Courier-BoldOblique", "Symbol", "ZapfDingbats",
    ];

    public static IReadOnlyDictionary<string, Func<byte[]>> All { get; } = new Dictionary<string, Func<byte[]>>(StringComparer.Ordinal)
    {
        ["pages-10000"] = PageCount10000,
        ["rotated-pages"] = RotatedPages,
        ["cropbox-offset"] = CropBoxOffset,
        ["broken-xref-offsets"] = () => PatternDocument().Build(1, xrefOffsetShift: 13),
        ["broken-startxref"] = () => PatternDocument().Build(1, startXrefOverride: 987_654),
        ["truncated"] = Truncated,
        ["garbage-with-header"] = GarbageWithHeader,
        ["huge-mediabox"] = HugeMediaBox,
        ["deep-gstate-nesting"] = DeepGraphicsStateNesting,
        ["transparency-layers"] = TransparencyLayers,
        ["text-standard-fonts"] = StandardFontText,
        ["engine-crash-form-fanout"] = EngineCrashFormFanOut,
        ["engine-hang-tiling-step"] = EngineHangTilingStep,
    };

    public static byte[] Generate(string name) =>
        All.TryGetValue(name, out var generate) ? generate() : throw new KeyNotFoundException($"There is no corpus generator named '{name}'.");

    private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Marks where a page's corners are: a red square top left, green top right, blue bottom left, a frame, and a label. Coordinates are those of the box it is drawn into.</summary>
    private static string Pattern(double x, double y, double width, double height, string label) =>
        $"q 0.93 0.95 0.88 rg {F(x)} {F(y)} {F(width)} {F(height)} re f Q\n" +
        $"1 0 0 rg {F(x + 10)} {F(y + height - 60)} 50 50 re f\n" +
        $"0 0.6 0 rg {F(x + width - 60)} {F(y + height - 60)} 50 50 re f\n" +
        $"0 0 1 rg {F(x + 10)} {F(y + 10)} 50 50 re f\n" +
        $"0 0 0 RG 3 w {F(x + 5)} {F(y + 5)} {F(width - 10)} {F(height - 10)} re S\n" +
        $"0.4 0.1 0.6 RG 1.5 w {F(x + 60)} {F(y + 60)} m {F(x + width - 60)} {F(y + height - 60)} l S\n" +
        $"0 g BT /F1 24 Tf {F(x + 80)} {F(y + (height / 2))} Td ({label}) Tj ET\n";

    private sealed record PageDefinition(string Entries, string Content);

    private static byte[] Document(IEnumerable<PageDefinition> pages, string pagesEntries = "", string extraResources = "")
    {
        var pdf = new PdfBuilder();
        var catalog = pdf.Reserve();
        var tree = pdf.Reserve();
        var font = pdf.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        var kids = new List<int>();
        foreach (var page in pages)
        {
            var stream = pdf.AddStream("", page.Content);
            kids.Add(pdf.Add($"<< /Type /Page /Parent {tree} 0 R /Contents {stream} 0 R /Resources << /Font << /F1 {font} 0 R >> {extraResources} >> {page.Entries} >>"));
        }

        pdf.Set(catalog, $"<< /Type /Catalog /Pages {tree} 0 R >>");
        pdf.Set(tree, $"<< /Type /Pages /Kids [{string.Join(' ', kids.Select(k => $"{k} 0 R"))}] /Count {kids.Count} {pagesEntries} >>");
        return pdf.Build(catalog);
    }

    /// <summary>The ordinary three-page file the structure-damage generators spoil.</summary>
    private static PdfBuilder PatternDocument()
    {
        // Rebuilt through the same helper so the bytes match Document(), but the builder is returned so a caller can damage the cross-reference table.
        var pdf = new PdfBuilder();
        var catalog = pdf.Reserve();
        var tree = pdf.Reserve();
        var font = pdf.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        var kids = new List<int>();
        for (var page = 1; page <= 3; page++)
        {
            var stream = pdf.AddStream("", Pattern(0, 0, 300, 400, $"Page {page}"));
            kids.Add(pdf.Add($"<< /Type /Page /Parent {tree} 0 R /Contents {stream} 0 R /MediaBox [0 0 300 400] /Resources << /Font << /F1 {font} 0 R >> >> >>"));
        }

        pdf.Set(catalog, $"<< /Type /Catalog /Pages {tree} 0 R >>");
        pdf.Set(tree, $"<< /Type /Pages /Kids [{string.Join(' ', kids.Select(k => $"{k} 0 R"))}] /Count {kids.Count} >>");
        return pdf;
    }

    private static byte[] PageCount10000()
    {
        var pages = Enumerable.Range(1, 10_000)
            .Select(n => new PageDefinition("", $"0.85 0.9 1 rg 0 0 240 120 re f 0 g BT /F1 40 Tf 16 40 Td (Page {n}) Tj ET\n"));
        return Document(pages, "/MediaBox [0 0 240 120]");
    }

    private static byte[] RotatedPages()
    {
        // Four explicit /Rotate values, then a fifth page that inherits /Rotate 90 from the page tree.
        var pages = new List<PageDefinition>();
        foreach (var rotation in new[] { 0, 90, 180, 270 })
        {
            pages.Add(new PageDefinition($"/Rotate {rotation}", Pattern(0, 0, 300, 200, $"Rotate {rotation}")));
        }

        pages.Add(new PageDefinition("", Pattern(0, 0, 300, 200, "Inherited 90")));
        return Document(pages, "/MediaBox [0 0 300 200] /Rotate 90");
    }

    private static byte[] CropBoxOffset()
    {
        var pages = new[]
        {
            // The MediaBox origin is negative and the CropBox sits inside it, away from the origin.
            new PageDefinition("/MediaBox [-100 -50 400 550] /CropBox [20 30 320 430]", Pattern(-100, -50, 500, 600, "Crop inside")),
            // Offset origin, a quarter turn, and a CropBox in the middle.
            new PageDefinition("/MediaBox [100 200 600 800] /CropBox [150 250 450 650] /Rotate 90", Pattern(100, 200, 500, 600, "Crop rotated")),
            // A CropBox that reaches outside the MediaBox: the visible page is their intersection.
            new PageDefinition("/MediaBox [0 0 300 400] /CropBox [-50 100 250 500]", Pattern(0, 0, 300, 400, "Crop outside")),
            // Upside-down corners in the box: lower-left and upper-right given in the "wrong" order.
            new PageDefinition("/MediaBox [300 400 0 0]", Pattern(0, 0, 300, 400, "Reversed box")),
        };
        return Document(pages);
    }

    private static byte[] Truncated()
    {
        var whole = PatternDocument().Build(1);
        return whole[..(whole.Length * 62 / 100)];
    }

    private static byte[] GarbageWithHeader()
    {
        var bytes = new byte[2048];
        var state = 0x2545F491u;
        for (var i = 0; i < bytes.Length; i++)
        {
            state = (state * 1664525u) + 1013904223u; // a fixed linear congruential sequence: the same bytes on every machine
            bytes[i] = (byte)(state >> 24);
        }

        var header = Encoding.Latin1.GetBytes("%PDF-1.7\n");
        return [.. header, .. bytes];
    }

    private static byte[] HugeMediaBox() =>
        Document([new PageDefinition("/MediaBox [0 0 14400 14400]", Pattern(0, 0, 14400, 14400, "Fourteen thousand four hundred points"))]);

    private static byte[] DeepGraphicsStateNesting()
    {
        // 6000 nested q operators before anything is drawn, then the matching Q operators: the interpreter's depth limit and its recovery.
        const int depth = 6000;
        var content = new StringBuilder();
        content.Append(string.Concat(Enumerable.Repeat("q ", depth)));
        content.Append(Pattern(0, 0, 300, 200, "Nested"));
        content.Append(string.Concat(Enumerable.Repeat("Q ", depth)));
        return Document([new PageDefinition("/MediaBox [0 0 300 200]", content.ToString())]);
    }

    private static byte[] TransparencyLayers()
    {
        const string extGState = "/ExtGState << /Multiply << /ca 0.6 /CA 0.6 /BM /Multiply >> /Screen << /ca 0.5 /BM /Screen >> /Plain << /ca 0.4 >> >>";
        var content = new StringBuilder(Pattern(0, 0, 400, 300, "Transparency"));
        string[] states = ["Multiply", "Screen", "Plain"];
        for (var layer = 0; layer < 36; layer++)
        {
            var x = 10 + ((layer % 6) * 55);
            var y = 10 + ((layer / 6) * 40);
            content.Append(CultureInfo.InvariantCulture, $"q /{states[layer % 3]} gs {F((layer % 5) / 5.0)} {F((layer % 7) / 7.0)} {F((layer % 3) / 3.0)} rg {x} {y} 120 90 re f Q\n");
        }

        return Document([new PageDefinition("/MediaBox [0 0 400 300]", content.ToString())], extraResources: extGState);
    }

    private static byte[] StandardFontText()
    {
        var pdf = new PdfBuilder();
        var catalog = pdf.Reserve();
        var tree = pdf.Reserve();
        var fonts = StandardFonts.Select((name, i) => (Name: name, Number: pdf.Add($"<< /Type /Font /Subtype /Type1 /BaseFont /{name} >>"), Index: i)).ToList();
        var content = new StringBuilder("0 g\n");
        foreach (var font in fonts)
        {
            content.Append(CultureInfo.InvariantCulture, $"BT /F{font.Index} 18 Tf 24 {640 - (font.Index * 42)} Td ({font.Name}: Sphinx of black quartz, judge my vow 0123456789) Tj ET\n");
        }

        var stream = pdf.AddStream("", content.ToString());
        var resources = string.Join(' ', fonts.Select(f => $"/F{f.Index} {f.Number} 0 R"));
        var page = pdf.Add($"<< /Type /Page /Parent {tree} 0 R /MediaBox [0 0 612 700] /Contents {stream} 0 R /Resources << /Font << {resources} >> >> >>");
        pdf.Set(catalog, $"<< /Type /Catalog /Pages {tree} 0 R >>");
        pdf.Set(tree, $"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
        return pdf.Build(catalog);
    }

    /// <summary>
    /// A file that takes the engine down by running it out of memory (the sandbox's job memory limit ends the process, and the supervisor replaces it). The page invokes a
    /// Form XObject that invokes the next one three times, thirty levels deep, each form a distinct object: PDFium's guards stop cycles and the nesting depth, but not
    /// this fan-out, and it builds a page-object tree of 3^30 leaves while it loads the page. It is a valid PDF, 6 KB long. Not a PDFium bug report: a stress shape the
    /// engine must contain, as a hostile file would.
    /// </summary>
    private static byte[] EngineCrashFormFanOut()
    {
        const int levels = 30;
        var pdf = new PdfBuilder();
        var catalog = pdf.Reserve();
        var tree = pdf.Reserve();
        var page = pdf.Reserve();
        var contents = pdf.AddStream("", "/X1 Do");
        var forms = Enumerable.Range(0, levels).Select(_ => pdf.Reserve()).ToArray();
        for (var level = 1; level <= levels; level++)
        {
            var last = level == levels;
            var resources = last ? "<< >>" : $"<< /XObject << /X{level + 1} {forms[level]} 0 R >> >>";
            var content = last ? "0 0 1 rg 10 10 5 5 re f" : $"/X{level + 1} Do /X{level + 1} Do /X{level + 1} Do";
            pdf.SetStream(forms[level - 1], $"/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Resources {resources}", Encoding.Latin1.GetBytes(content));
        }

        pdf.Set(page, $"<< /Type /Page /Parent {tree} 0 R /MediaBox [0 0 200 200] /Contents {contents} 0 R /Resources << /XObject << /X1 {forms[0]} 0 R >> >> >>");
        pdf.Set(catalog, $"<< /Type /Catalog /Pages {tree} 0 R >>");
        pdf.Set(tree, $"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
        return pdf.Build(catalog);
    }

    /// <summary>
    /// A file that makes PDFium spin inside one drawing operation: a tiling pattern whose step is 0.0001 point, filled over a 14400-point page. PDFium checks its
    /// render deadline about every 100 operations, so a single operation that never ends outlasts any deadline, and the engine's watchdog has to notice that the
    /// request went unanswered and kill the process. On the machine this was made on it ran for over 200 seconds without finishing; whether it would ever end was not
    /// established, and the gate never waits to find out. The reference renderer would spin the same way, so the manifest turns it off for this file.
    /// </summary>
    private static byte[] EngineHangTilingStep()
    {
        var pdf = new PdfBuilder();
        var catalog = pdf.Reserve();
        var tree = pdf.Reserve();
        var contents = pdf.AddStream("", "/Pattern cs /P1 scn 0 0 14400 14400 re f");
        var pattern = pdf.AddStream("/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 1 1] /XStep 0.0001 /YStep 0.0001 /Resources << >>", "1 0 0 rg 0 0 1 1 re f");
        var page = pdf.Add($"<< /Type /Page /Parent {tree} 0 R /MediaBox [0 0 14400 14400] /Contents {contents} 0 R /Resources << /Pattern << /P1 {pattern} 0 R >> >> >>");
        pdf.Set(catalog, $"<< /Type /Catalog /Pages {tree} 0 R >>");
        pdf.Set(tree, $"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
        return pdf.Build(catalog);
    }
}
