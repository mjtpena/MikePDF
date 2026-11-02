using System.Globalization;
using System.Text;

namespace MikePDF.Integration.Tests.Printing;

/// <summary>Builds the PDFs the print tests need: mixed sizes and rotations, an annotated page, a filled form, hostile print preferences, a long text document. Nothing is stored in the repository.</summary>
internal static class PrintFixtures
{
    private static readonly string[] Words = "print job band render spooler driver duplex collate scale page range device raster vector engine broker sandbox memory flat fidelity annotation form field".Split(' ');

    /// <summary>The 0-based page numbers of <see cref="Mix"/>: Letter, A4, Letter landscape, A4 with /Rotate 90, Letter with a CropBox away from the origin, a 30 x 40 inch poster.</summary>
    public const int MixLetter = 0;
    public const int MixA4 = 1;
    public const int MixLandscape = 2;
    public const int MixRotated = 3;
    public const int MixCropped = 4;
    public const int MixPoster = 5;

    private sealed class Builder
    {
        private readonly List<byte[]?> _objects = [];

        public int Count => _objects.Count;

        public int Add(string body)
        {
            _objects.Add(Encoding.Latin1.GetBytes(body));
            return _objects.Count;
        }

        public int Reserve()
        {
            _objects.Add(null);
            return _objects.Count;
        }

        public void Set(int number, string body) => _objects[number - 1] = Encoding.Latin1.GetBytes(body);

        public int Stream(string dictionary, string data)
        {
            var content = Encoding.Latin1.GetBytes(data);
            _objects.Add([.. Encoding.Latin1.GetBytes($"<< {dictionary} /Length {content.Length} >>\nstream\n"), .. content, .. Encoding.Latin1.GetBytes("\nendstream")]);
            return _objects.Count;
        }

        public byte[] Build(int root)
        {
            using var pdf = new MemoryStream();
            pdf.Write(Encoding.Latin1.GetBytes("%PDF-1.7\n"));
            var offsets = new List<long>();
            for (var i = 0; i < _objects.Count; i++)
            {
                offsets.Add(pdf.Position);
                pdf.Write(Encoding.Latin1.GetBytes($"{i + 1} 0 obj\n"));
                pdf.Write(_objects[i]!);
                pdf.Write(Encoding.Latin1.GetBytes("\nendobj\n"));
            }

            var xref = pdf.Position;
            var table = new StringBuilder($"xref\n0 {_objects.Count + 1}\n0000000000 65535 f \n");
            foreach (var offset in offsets)
            {
                table.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
            }

            table.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {_objects.Count + 1} /Root {root} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            pdf.Write(Encoding.Latin1.GetBytes(table.ToString()));
            return pdf.ToArray();
        }
    }

    private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>A frame at the edge of the box, diagonals, a label, a ruler, lines of text, a gradient bar and an image: errors in scale, offset and rotation all show.</summary>
    private static string Content(double x0, double y0, double x1, double y1, string label, int seed, int textLines)
    {
        var w = x1 - x0;
        var h = y1 - y0;
        var sb = new StringBuilder();
        var random = new Random(seed);
        sb.Append(CultureInfo.InvariantCulture, $"1 0 0 RG {F(Math.Max(2, w / 150))} w {F(x0 + 1)} {F(y0 + 1)} {F(w - 2)} {F(h - 2)} re S\n");
        sb.Append(CultureInfo.InvariantCulture, $"0 0.4 0.8 RG 0.5 w {F(x0)} {F(y0)} m {F(x1)} {F(y1)} l S {F(x0)} {F(y1)} m {F(x1)} {F(y0)} l S\n");
        var fontSize = Math.Max(10, Math.Min(w, h) / 10);
        sb.Append(CultureInfo.InvariantCulture, $"BT /F2 {F(fontSize)} Tf 0 g {F(x0 + (w * 0.08))} {F(y1 - (h * 0.1))} Td ({label}) Tj ET\n");
        for (var t = 0.0; t <= w; t += 72)
        {
            sb.Append(CultureInfo.InvariantCulture, $"0 g 0.5 w {F(x0 + t)} {F(y0)} m {F(x0 + t)} {F(y0 + (((int)(t / 72) % 5 == 0) ? 14 : 7))} l S\n");
        }

        var lines = Math.Min(textLines, (int)((h * 0.7) / 13));
        var y = y1 - (h * 0.18);
        for (var line = 0; line < lines; line++)
        {
            var text = new StringBuilder();
            for (var i = 0; i < 8 + random.Next(7); i++)
            {
                text.Append(Words[random.Next(Words.Length)]).Append(' ');
            }

            sb.Append(CultureInfo.InvariantCulture, $"BT /F1 10 Tf 0 g {F(x0 + (w * 0.08))} {F(y)} Td ({text.ToString().TrimEnd()}) Tj ET\n");
            y -= 13;
        }

        for (var i = 0; i < 40; i++)
        {
            var g = i / 39.0;
            sb.Append(CultureInfo.InvariantCulture, $"{F(g)} {F(1 - g)} 0.5 rg {F(x0 + (w * 0.08) + (i * (w * 0.84 / 40)))} {F(y0 + (h * 0.05))} {F((w * 0.84 / 40) + 0.3)} {F(h * 0.04)} re f\n");
        }

        sb.Append(CultureInfo.InvariantCulture, $"q {F(w * 0.3)} 0 0 {F(w * 0.3)} {F(x0 + (w * 0.6))} {F(y0 + (h * 0.12))} cm /Im0 Do Q\n");
        return sb.ToString();
    }

    private static string ImageData()
    {
        var sb = new StringBuilder(60 * 60 * 3);
        for (var y = 0; y < 60; y++)
        {
            for (var x = 0; x < 60; x++)
            {
                sb.Append((char)(x * 4)).Append((char)(y * 4)).Append((char)((x + y) * 2 % 256));
            }
        }

        return sb.ToString();
    }

    private static (Builder Pdf, int Resources) Start()
    {
        var pdf = new Builder();
        pdf.Add("<< /Type /Catalog /Pages 2 0 R >>");
        pdf.Reserve();
        pdf.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        pdf.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>");
        var image = pdf.Stream("/Type /XObject /Subtype /Image /Width 60 /Height 60 /ColorSpace /DeviceRGB /BitsPerComponent 8", ImageData());
        var resources = pdf.Add($"<< /Font << /F1 3 0 R /F2 4 0 R >> /XObject << /Im0 {image} 0 R >> >>");
        return (pdf, resources);
    }

    private static void Finish(Builder pdf, List<int> pages, string catalogExtra = "")
    {
        pdf.Set(2, $"<< /Type /Pages /Kids [{string.Join(' ', pages.Select(p => p + " 0 R"))}] /Count {pages.Count} >>");
        if (catalogExtra.Length > 0)
        {
            pdf.Set(1, $"<< /Type /Catalog /Pages 2 0 R {catalogExtra} >>");
        }
    }

    private static int Page(Builder pdf, int resources, string mediaBox, string extra, string content, string pageExtra = "")
    {
        var stream = pdf.Stream(string.Empty, content);
        return pdf.Add($"<< /Type /Page /Parent 2 0 R /MediaBox {mediaBox} {extra} /Resources {resources} 0 R /Contents {stream} 0 R {pageExtra} >>");
    }

    /// <summary>Six pages of different sizes, rotations and boxes (see the <c>Mix*</c> constants).</summary>
    public static byte[] Mix(bool withPoster = true)
    {
        var (pdf, resources) = Start();
        var pages = new List<int>
        {
            Page(pdf, resources, "[0 0 612 792]", "", Content(0, 0, 612, 792, "1 Letter portrait", 1, 40)),
            Page(pdf, resources, "[0 0 595.28 841.89]", "", Content(0, 0, 595.28, 841.89, "2 A4 portrait", 2, 40)),
            Page(pdf, resources, "[0 0 792 612]", "", Content(0, 0, 792, 612, "3 Letter landscape", 3, 30)),
            Page(pdf, resources, "[0 0 595.28 841.89]", "/Rotate 90", Content(0, 0, 595.28, 841.89, "4 A4 Rotate 90", 4, 40)),
            Page(pdf, resources, "[0 0 700 900]", "/CropBox [50 30 662 822]", Content(50, 30, 662, 822, "5 Crop 50 30", 5, 40)),
        };
        if (withPoster)
        {
            pages.Add(Page(pdf, resources, "[0 0 2160 2880]", "", Content(0, 0, 2160, 2880, "6 Poster 30 x 40 in", 6, 40)));
        }

        Finish(pdf, pages);
        return pdf.Build(1);
    }

    /// <summary>Letter pages with text, a gradient bar and an image.</summary>
    public static byte[] Text(int pageCount)
    {
        var (pdf, resources) = Start();
        var pages = Enumerable.Range(0, pageCount).Select(i => Page(pdf, resources, "[0 0 612 792]", "", Content(0, 0, 612, 792, $"Page {i + 1}", i + 1, 48))).ToList();
        Finish(pdf, pages);
        return pdf.Build(1);
    }

    /// <summary>
    /// One Letter page with two square annotations of 100 x 100 points: a red one at (100, 600) with the Print flag, and a blue one at (300, 600) without it (shown on screen, never printed).
    /// Both have an explicit appearance stream, so what is drawn does not depend on PDFium making one.
    /// </summary>
    public static byte[] Annotated()
    {
        var (pdf, resources) = Start();
        var redAppearance = pdf.Stream("/Type /XObject /Subtype /Form /BBox [0 0 100 100]", "1 0 0 rg 0 0 100 100 re f");
        var blueAppearance = pdf.Stream("/Type /XObject /Subtype /Form /BBox [0 0 100 100]", "0 0 1 rg 0 0 100 100 re f");
        var printed = pdf.Add($"<< /Type /Annot /Subtype /Square /Rect [100 600 200 700] /F 4 /AP << /N {redAppearance} 0 R >> >>");
        var screenOnly = pdf.Add($"<< /Type /Annot /Subtype /Square /Rect [300 600 400 700] /F 0 /AP << /N {blueAppearance} 0 R >> >>");
        var page = Page(pdf, resources, "[0 0 612 792]", string.Empty, "0.8 g 50 50 512 40 re f\n", $"/Annots [{printed} 0 R {screenOnly} 0 R]");
        Finish(pdf, [page]);
        return pdf.Build(1);
    }

    /// <summary>
    /// One Letter page with a text field (a 200 x 50 point widget at (100, 600)) whose appearance is a green block on its left half and nothing on its right half. The right half is where
    /// a form field's editing highlight would show; printing must leave it white.
    /// </summary>
    public static byte[] Form()
    {
        var (pdf, resources) = Start();
        var appearance = pdf.Stream("/Type /XObject /Subtype /Form /BBox [0 0 200 50]", "0 0.6 0 rg 0 0 100 50 re f");
        var field = pdf.Add($"<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /V (Hello) /Rect [100 600 300 650] /F 4 /DA (/Helv 0 Tf 0 g) /AP << /N {appearance} 0 R >> >>");
        var page = Page(pdf, resources, "[0 0 612 792]", string.Empty, "0.8 g 50 50 512 40 re f\n", $"/Annots [{field} 0 R]");
        Finish(pdf, [page], $"/AcroForm << /Fields [{field} 0 R] /DA (/Helv 0 Tf 0 g) >>");
        return pdf.Build(1);
    }

    /// <summary>A document that lies about how it wants to be printed: 2,147,483,647 copies, a page range with negative and huge numbers, and an unknown duplex name.</summary>
    public static byte[] HostilePreferences()
    {
        var (pdf, resources) = Start();
        var pages = new List<int> { Page(pdf, resources, "[0 0 612 792]", string.Empty, Content(0, 0, 612, 792, "Hostile", 9, 5)), Page(pdf, resources, "[0 0 612 792]", string.Empty, Content(0, 0, 612, 792, "Hostile 2", 10, 5)) };
        Finish(pdf, pages, "/ViewerPreferences << /NumCopies 2147483647 /PrintPageRange [-5 4000000000 7 3] /Duplex /Sideways /PrintScaling /None >>");
        return pdf.Build(1);
    }

    /// <summary>One page of the given size in points, with its frame at the edge, for hostile page sizes.</summary>
    public static byte[] OnePage(double width, double height)
    {
        var (pdf, resources) = Start();
        var page = Page(pdf, resources, $"[0 0 {F(width)} {F(height)}]", string.Empty, Content(0, 0, width, height, "One page", 11, 10));
        Finish(pdf, [page]);
        return pdf.Build(1);
    }
}
