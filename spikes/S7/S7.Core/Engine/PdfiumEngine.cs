using System.Runtime.InteropServices;
using System.Text;

namespace S7.Core.Engine;

/// <summary>
/// Stand-in for the sandboxed engine process. Everything it returns is plain data
/// (PageTextData / RenderedPage); the accessibility layer never touches a PDFium handle.
/// </summary>
public sealed unsafe class PdfiumEngine : IDisposable
{
    private static readonly object InitLock = new();
    private static bool _init;
    private readonly byte[] _pdf;
    private GCHandle _pin;
    private IntPtr _doc;

    public PdfiumEngine(byte[] pdf)
    {
        lock (InitLock) { if (!_init) { Pdfium.FPDF_InitLibrary(); _init = true; } }
        _pdf = pdf;
        _pin = GCHandle.Alloc(pdf, GCHandleType.Pinned);
        fixed (byte* pw = new byte[1])
            _doc = Pdfium.FPDF_LoadMemDocument((byte*)_pin.AddrOfPinnedObject(), pdf.Length, pw);
        if (_doc == IntPtr.Zero)
            throw new InvalidOperationException($"FPDF_LoadMemDocument failed, FPDF_GetLastError={Pdfium.FPDF_GetLastError()}");
    }

    public int PageCount => Pdfium.FPDF_GetPageCount(_doc);

    public static PdfiumEngine Open(string path) => new(File.ReadAllBytes(path));

    public void Dispose()
    {
        if (_doc != IntPtr.Zero) { Pdfium.FPDF_CloseDocument(_doc); _doc = IntPtr.Zero; }
        if (_pin.IsAllocated) _pin.Free();
    }

    public PageTextData ExtractPage(int pageIndex)
    {
        var page = Pdfium.FPDF_LoadPage(_doc, pageIndex);
        if (page == IntPtr.Zero) throw new InvalidOperationException("FPDF_LoadPage failed");
        try
        {
            var box = ReadBox(page);
            var tp = Pdfium.FPDFText_LoadPage(page);
            if (tp == IntPtr.Zero) throw new InvalidOperationException("FPDFText_LoadPage failed");
            try
            {
                int n = Pdfium.FPDFText_CountChars(tp);
                var chars = new List<PdfChar>(n);
                var mcidCache = new Dictionary<IntPtr, int>();
                for (int i = 0; i < n; i++)
                {
                    uint cp = Pdfium.FPDFText_GetUnicode(tp, i);
                    string text = cp == 0 ? "" : CodePointToString(cp);
                    double l = 0, r = 0, b = 0, t = 0;
                    Pdfium.FPDFText_GetCharBox(tp, i, &l, &r, &b, &t);
                    Pdfium.FsRectF lr = default;
                    var loose = Pdfium.FPDFText_GetLooseCharBox(tp, i, &lr) != 0 ? new UserRect(lr.Left, lr.Bottom, lr.Right, lr.Top) : new UserRect(l, b, r, t);
                    bool gen = Pdfium.FPDFText_IsGenerated(tp, i) == 1;
                    bool hyph = Pdfium.FPDFText_IsHyphen(tp, i) == 1;
                    int mcid = -1;
                    if (!gen)
                    {
                        var obj = Pdfium.FPDFText_GetTextObject(tp, i);
                        if (obj != IntPtr.Zero)
                        {
                            if (!mcidCache.TryGetValue(obj, out mcid))
                                mcidCache[obj] = mcid = ReadMcid(obj);
                        }
                    }
                    chars.Add(new PdfChar(i, text, new UserRect(l, b, r, t), loose, gen, hyph, mcid,
                        Pdfium.FPDFText_GetFontSize(tp, i), Pdfium.FPDFText_GetCharAngle(tp, i)));
                }

                string raw = "";
                if (n > 0)
                {
                    var buf = new ushort[n + 1];
                    fixed (ushort* p = buf) { int w = Pdfium.FPDFText_GetText(tp, 0, n, p); raw = new string((char*)p, 0, Math.Max(0, w - 1)); }
                }

                var (tagged, blocks) = ReadStructTree(page);
                return new PageTextData
                {
                    PageIndex = pageIndex,
                    Box = box,
                    Chars = chars,
                    IsTagged = tagged,
                    StructBlocks = blocks,
                    PdfiumRawText = raw,
                };
            }
            finally { Pdfium.FPDFText_ClosePage(tp); }
        }
        finally { Pdfium.FPDF_ClosePage(page); }
    }

    private static string CodePointToString(uint cp)
    {
        if (cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF)) return cp <= 0xFFFF ? ((char)cp).ToString() : "";
        return char.ConvertFromUtf32((int)cp);
    }

    private static PageBox ReadBox(IntPtr page)
    {
        float l, b, r, t;
        UserRect media = default, crop = default;
        if (Pdfium.FPDFPage_GetMediaBox(page, &l, &b, &r, &t) != 0) media = new UserRect(l, b, r, t);
        // FPDF_GetPageBoundingBox = intersection of media box and crop box, i.e. what PDFium actually displays.
        Pdfium.FsRectF bb;
        if (Pdfium.FPDF_GetPageBoundingBox(page, &bb) != 0) crop = new UserRect(bb.Left, bb.Bottom, bb.Right, bb.Top);
        // FS_RECTF documents top/bottom as "left-top" / "right-bottom" but page space has top > bottom; normalise.
        crop = new UserRect(Math.Min(crop.Left, crop.Right), Math.Min(crop.Bottom, crop.Top), Math.Max(crop.Left, crop.Right), Math.Max(crop.Bottom, crop.Top));
        int rot = Pdfium.FPDFPage_GetRotation(page);          // 0..3 (multiples of 90 deg clockwise)
        return new PageBox { Media = media, Crop = crop, Rotate = ((rot % 4) + 4) % 4 * 90 };
    }

    private static int ReadMcid(IntPtr obj)
    {
        int marks = Pdfium.FPDFPageObj_CountMarks(obj);
        int found = -1;
        byte[] key = Encoding.ASCII.GetBytes("MCID\0");
        for (int m = 0; m < marks; m++)
        {
            var mark = Pdfium.FPDFPageObj_GetMark(obj, (uint)m);
            if (mark == IntPtr.Zero) continue;
            int v;
            fixed (byte* k = key)
                if (Pdfium.FPDFPageObjMark_GetParamIntValue(mark, k, &v) != 0) found = v; // innermost mark with an MCID wins
        }
        return found;
    }

    private static string StructString(IntPtr el, int which)
    {
        uint need = which switch
        {
            0 => Pdfium.FPDF_StructElement_GetType(el, null, 0),
            1 => Pdfium.FPDF_StructElement_GetAltText(el, null, 0),
            _ => Pdfium.FPDF_StructElement_GetActualText(el, null, 0),
        };
        if (need <= 2) return "";
        var buf = new byte[need];
        fixed (byte* p = buf)
        {
            _ = which switch
            {
                0 => Pdfium.FPDF_StructElement_GetType(el, p, need),
                1 => Pdfium.FPDF_StructElement_GetAltText(el, p, need),
                _ => Pdfium.FPDF_StructElement_GetActualText(el, p, need),
            };
        }
        return Encoding.Unicode.GetString(buf, 0, (int)need - 2);
    }

    // Block-level structure types: a block's text is everything beneath it, up to the next block.
    private static readonly HashSet<string> BlockTypes = new(StringComparer.Ordinal)
    {
        "P","H","H1","H2","H3","H4","H5","H6","LI","Lbl","LBody","TD","TH","Caption","Figure","Formula","Code","BlockQuote","Quote","Title","TOCI","Note","Reference","BibEntry",
    };

    private static (bool tagged, IReadOnlyList<StructBlock> blocks) ReadStructTree(IntPtr page)
    {
        var tree = Pdfium.FPDF_StructTree_GetForPage(page);
        if (tree == IntPtr.Zero) return (false, Array.Empty<StructBlock>());
        try
        {
            int n = Pdfium.FPDF_StructTree_CountChildren(tree);
            if (n <= 0) return (false, Array.Empty<StructBlock>());
            var blocks = new List<StructBlock>();
            for (int i = 0; i < n; i++)
            {
                var el = Pdfium.FPDF_StructTree_GetChildAtIndex(tree, i);
                if (el != IntPtr.Zero) Walk(el, null, blocks, 0);
            }
            return (true, blocks);
        }
        finally { Pdfium.FPDF_StructTree_Close(tree); }
    }

    private sealed class Pending { public string Type = ""; public List<int> Mcids = new(); public string? Alt, Actual; }

    private static void Walk(IntPtr el, Pending? cur, List<StructBlock> sink, int depth)
    {
        if (depth > 64) return;
        string type = StructString(el, 0);
        Pending? mine = cur;
        bool opened = false;
        if (BlockTypes.Contains(type) || cur == null)
        {
            // Start a new block unless we are already inside a leaf block of this kind (e.g. LI containing LBody).
            mine = new Pending { Type = type, Alt = NullIfEmpty(StructString(el, 1)), Actual = NullIfEmpty(StructString(el, 2)) };
            opened = true;
        }
        // Children in document order. Elements and raw MCIDs are interleaved in /K, so walk by child index.
        int kids = Pdfium.FPDF_StructElement_CountChildren(el);
        if (kids < 0) kids = 0;
        bool flushedForNested = false;
        for (int k = 0; k < kids; k++)
        {
            var child = Pdfium.FPDF_StructElement_GetChildAtIndex(el, k);
            if (child != IntPtr.Zero)
            {
                // A nested block-level element interrupts the current block: flush what we have so far.
                string ct = StructString(child, 0);
                if (opened && BlockTypes.Contains(ct) && mine!.Mcids.Count > 0) { sink.Add(ToBlock(mine)); mine = new Pending { Type = type }; flushedForNested = true; }
                Walk(child, mine, sink, depth + 1);
            }
            else
            {
                int mcid = Pdfium.FPDF_StructElement_GetChildMarkedContentID(el, k);
                if (mcid >= 0) mine!.Mcids.Add(mcid);
            }
        }
        _ = flushedForNested;
        if (opened && mine!.Mcids.Count > 0) sink.Add(ToBlock(mine));
    }

    private static StructBlock ToBlock(Pending p) => new(p.Type, p.Mcids.ToArray(), p.Alt, p.Actual);
    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    /// <summary>Renders the page with FPDF_RenderPageBitmap onto a white BGRA bitmap. rotate = extra view rotation (0..3, 90 deg CW steps) on top of /Rotate.</summary>
    public RenderedPage Render(int pageIndex, double scale, int viewRotate)
    {
        var page = Pdfium.FPDF_LoadPage(_doc, pageIndex);
        try
        {
            var (w, h) = DeviceSize(page, scale, viewRotate);
            var bmp = Pdfium.FPDFBitmap_Create(w, h, 0);
            try
            {
                Pdfium.FPDFBitmap_FillRect(bmp, 0, 0, w, h, 0xFFFFFFFF);
                Pdfium.FPDF_RenderPageBitmap(bmp, page, 0, 0, w, h, viewRotate, 0x01 /*FPDF_ANNOT*/);
                int stride = Pdfium.FPDFBitmap_GetStride(bmp);
                var outBuf = new byte[w * h * 4];
                byte* src = (byte*)Pdfium.FPDFBitmap_GetBuffer(bmp);
                for (int y = 0; y < h; y++)
                    Marshal.Copy((IntPtr)(src + (long)y * stride), outBuf, y * w * 4, w * 4);
                for (int i = 3; i < outBuf.Length; i += 4) outBuf[i] = 255; // format is BGRx for alpha=0
                return new RenderedPage { Width = w, Height = h, Bgra = outBuf };
            }
            finally { Pdfium.FPDFBitmap_Destroy(bmp); }
        }
        finally { Pdfium.FPDF_ClosePage(page); }
    }

    private static (int w, int h) DeviceSize(IntPtr page, double scale, int viewRotate)
    {
        // FPDF_GetPageWidthF/HeightF already account for /Rotate. View rotation by 90/270 swaps them.
        double pw = Pdfium.FPDF_GetPageWidthF(page), ph = Pdfium.FPDF_GetPageHeightF(page);
        if ((viewRotate & 1) == 1) (pw, ph) = (ph, pw);
        return ((int)Math.Round(pw * scale), (int)Math.Round(ph * scale));
    }

    /// <summary>
    /// Cross-check helper: PDFium's own PageToDevice for a user-space point. Used only by the tests to validate
    /// the independent transform in <see cref="S7.Core.Geometry.PageGeometry"/>. Integer result (PDFium rounds).
    /// </summary>
    public (int x, int y) PdfiumPageToDevice(int pageIndex, int deviceW, int deviceH, int viewRotate, double ux, double uy)
    {
        var page = Pdfium.FPDF_LoadPage(_doc, pageIndex);
        try
        {
            int dx = 0, dy = 0;
            Pdfium.FPDF_PageToDevice(page, 0, 0, deviceW, deviceH, viewRotate, ux, uy, &dx, &dy);
            return (dx, dy);
        }
        finally { Pdfium.FPDF_ClosePage(page); }
    }

    public (double x, double y) PdfiumDeviceToPage(int pageIndex, int deviceW, int deviceH, int viewRotate, int dx, int dy)
    {
        var page = Pdfium.FPDF_LoadPage(_doc, pageIndex);
        try
        {
            double px = 0, py = 0;
            Pdfium.FPDF_DeviceToPage(page, 0, 0, deviceW, deviceH, viewRotate, dx, dy, &px, &py);
            return (px, py);
        }
        finally { Pdfium.FPDF_ClosePage(page); }
    }

    public (int w, int h) DeviceSizeFor(int pageIndex, double scale, int viewRotate)
    {
        var page = Pdfium.FPDF_LoadPage(_doc, pageIndex);
        try { return DeviceSize(page, scale, viewRotate); }
        finally { Pdfium.FPDF_ClosePage(page); }
    }
}
