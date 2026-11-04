using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MikePDF.PdfWriter.Tests.Support;

// What a reader sees of one page: the unique text marker of the fixtures ("PAGE-A-03") found in its text, its size as displayed, its boxes, label, links (the marker of the page each goes to, or "uri", "none", "unresolved") and form widgets (name, field type, value, read through the form-fill layer).
internal sealed record PdfiumPage(string? Key, string Text, double Width, double Height, int Rotation, string Media, string Crop, string Label, IReadOnlyList<string> Links, IReadOnlyList<(string Name, int Type, string Value)> Widgets, string RenderHash);

internal sealed record PdfiumBookmark(int Depth, string Title, string Target);

internal sealed record PdfiumProfile(int PageCount, IReadOnlyList<PdfiumPage> Pages, IReadOnlyList<PdfiumBookmark> Outline, IReadOnlyDictionary<string, string> NamedDestinations)
{
    public IReadOnlyList<string?> Keys => [.. Pages.Select(static p => p.Key)];
}

/// <summary>
/// The reading half of the test-only PDFium surface (the fresh-engine check): reopens bytes in a new PDFium document and reports pages, their text,
/// boxes, rotation, labels, links, widgets, the outline and the named destinations. Everything is read inside one lock (PDFium is not thread-safe) and
/// no handle escapes. Signatures are from fpdfview.h, fpdf_doc.h, fpdf_text.h, fpdf_annot.h, fpdf_formfill.h and fpdf_transformpage.h of the pinned build
/// (chromium/8125); on Windows <c>unsigned long</c> and <c>long</c> are 32 bits.
/// </summary>
internal static unsafe partial class Pdfium
{
    private static readonly Regex KeyPattern = new(@"PAGE-[A-Z0-9]+-\d+", RegexOptions.Compiled);

    /// <summary>Reads <paramref name="bytes"/> in a fresh PDFium document. Null when PDFium refuses the file.</summary>
    public static PdfiumProfile? Profile(byte[] bytes, bool render = true, int maxPages = int.MaxValue)
    {
        EnsureLoaded();
        lock (Gate)
        {
            var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            nint document = 0;
            nint formInfo = 0;
            nint form = 0;
            try
            {
                document = FPDF_LoadMemDocument64(pin.AddrOfPinnedObject(), (nuint)bytes.Length, 0);
                if (document == 0)
                    return null;
                // FPDF_FORMFILLINFO version 1 with every callback null: headless form reading, no timers, no UI.
                formInfo = Marshal.AllocHGlobal(1024);
                new Span<byte>((void*)formInfo, 1024).Clear();
                *(int*)formInfo = 1;
                form = FPDFDOC_InitFormFillEnvironment(document, formInfo);
                return ReadProfile(document, form, render, maxPages);
            }
            finally
            {
                if (form != 0)
                    FPDFDOC_ExitFormFillEnvironment(form);
                if (document != 0)
                    FPDF_CloseDocument(document);
                if (formInfo != 0)
                    Marshal.FreeHGlobal(formInfo);
                pin.Free();
            }
        }
    }

    private static PdfiumProfile ReadProfile(nint document, nint form, bool render, int maxPages)
    {
        var count = FPDF_GetPageCount(document);
        var read = Math.Min(count, maxPages); // only the first pages are described; the page count is always the whole document's
        var texts = new string[count];
        var keys = new string?[count];
        for (var i = 0; i < read; i++)
        {
            var page = FPDF_LoadPage(document, i);
            var textPage = FPDFText_LoadPage(page);
            texts[i] = ReadPageText(textPage);
            var match = KeyPattern.Match(texts[i]);
            keys[i] = match.Success ? match.Value : null;
            FPDFText_ClosePage(textPage);
            FPDF_ClosePage(page);
        }

        string Target(nint dest, nint action)
        {
            if (dest == 0 && action != 0)
            {
                var type = FPDFAction_GetType(action);
                if (type == 3)
                    return "uri";
                if (type != 1)
                    return "action:" + type;
                dest = FPDFAction_GetDest(document, action);
            }

            if (dest == 0)
                return "none";
            var index = FPDFDest_GetDestPageIndex(document, dest);
            return index < 0 || index >= count ? "unresolved" : keys[index] ?? "page#" + index;
        }

        var pages = new List<PdfiumPage>();
        for (var i = 0; i < read; i++)
        {
            var page = FPDF_LoadPage(document, i);
            if (form != 0)
                FORM_OnAfterLoadPage(page, form);
            float left, bottom, right, top;
            // PDFium answers for a box set on the page itself, not one inherited from the page tree: "-" is "not on the page".
            var media = FPDFPage_GetMediaBox(page, &left, &bottom, &right, &top) != 0
                ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{left:0.##},{bottom:0.##},{right:0.##},{top:0.##}")
                : "-";
            var crop = FPDFPage_GetCropBox(page, &left, &bottom, &right, &top) != 0
                ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{left:0.##},{bottom:0.##},{right:0.##},{top:0.##}")
                : "-";
            var widgets = new List<(string, int, string)>();
            for (var a = 0; a < FPDFPage_GetAnnotCount(page); a++)
            {
                var annotation = FPDFPage_GetAnnot(page, a);
                if (FPDFAnnot_GetSubtype(annotation) == 20 && form != 0)
                    widgets.Add((FormString(form, annotation, FPDFAnnot_GetFormFieldName), FPDFAnnot_GetFormFieldType(form, annotation), FormString(form, annotation, FPDFAnnot_GetFormFieldValue)));
                FPDFPage_CloseAnnot(annotation);
            }

            var links = new List<string>();
            var position = 0;
            nint link;
            while (FPDFLink_Enumerate(page, &position, &link) != 0)
            {
                var dest = FPDFLink_GetDest(document, link);
                links.Add(Target(dest, dest == 0 ? FPDFLink_GetAction(link) : 0));
            }

            var hash = render ? RenderHash(page) : "";
            pages.Add(new PdfiumPage(
                keys[i], texts[i], FPDF_GetPageWidthF(page), FPDF_GetPageHeightF(page), FPDFPage_GetRotation(page), media, crop,
                PageLabel(document, i), links, widgets, hash));
            if (form != 0)
                FORM_OnBeforeClosePage(page, form);
            FPDF_ClosePage(page);
        }

        var outline = new List<PdfiumBookmark>();
        Walk(0, 0);

        void Walk(nint parent, int depth)
        {
            var bookmark = FPDFBookmark_GetFirstChild(document, parent);
            while (bookmark != 0 && outline.Count < 10_000)
            {
                var dest = FPDFBookmark_GetDest(document, bookmark);
                outline.Add(new PdfiumBookmark(depth, BookmarkTitle(bookmark), Target(dest, dest == 0 ? FPDFBookmark_GetAction(bookmark) : 0)));
                if (depth < 16)
                    Walk(bookmark, depth + 1);
                bookmark = FPDFBookmark_GetNextSibling(document, bookmark);
            }
        }

        var named = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var namedCount = (int)FPDF_CountNamedDests(document);
        for (var i = 0; i < namedCount; i++)
        {
            var length = 0;
            FPDF_GetNamedDest(document, i, null, &length);
            if (length <= 0)
                continue;
            var buffer = new byte[length];
            nint dest;
            fixed (byte* p = buffer)
                dest = FPDF_GetNamedDest(document, i, p, &length);
            named[Utf16(buffer, length)] = Target(dest, 0);
        }

        return new PdfiumProfile(count, pages, outline, named);
    }

    private static string Utf16(byte[] buffer, int length) => length < 2 ? "" : Encoding.Unicode.GetString(buffer, 0, length - 2);

    private static string ReadPageText(nint textPage)
    {
        var buffer = new ushort[4001];
        fixed (ushort* p = buffer)
        {
            var n = FPDFText_GetText(textPage, 0, 4000, p);
            return n <= 1 ? "" : new string((char*)p, 0, n - 1);
        }
    }

    private static string BookmarkTitle(nint bookmark)
    {
        var n = FPDFBookmark_GetTitle(bookmark, null, 0);
        if (n <= 2)
            return "";
        var buffer = new byte[n];
        fixed (byte* p = buffer)
            _ = FPDFBookmark_GetTitle(bookmark, p, n);
        return Utf16(buffer, (int)n);
    }

    private static string PageLabel(nint document, int page)
    {
        var n = FPDF_GetPageLabel(document, page, null, 0);
        if (n <= 2)
            return "";
        var buffer = new byte[n];
        fixed (byte* p = buffer)
            _ = FPDF_GetPageLabel(document, page, p, n);
        return Utf16(buffer, (int)n);
    }

    private delegate uint FormStringReader(nint form, nint annotation, byte* buffer, uint length);

    private static string FormString(nint form, nint annotation, FormStringReader read)
    {
        var n = read(form, annotation, null, 0);
        if (n <= 2)
            return "";
        var buffer = new byte[n];
        fixed (byte* p = buffer)
            read(form, annotation, p, n);
        return Utf16(buffer, (int)n);
    }

    private static string RenderHash(nint page)
    {
        var width = Math.Max(1, (int)Math.Round(FPDF_GetPageWidthF(page) * 0.5));
        var height = Math.Max(1, (int)Math.Round(FPDF_GetPageHeightF(page) * 0.5));
        var bitmap = FPDFBitmap_Create(width, height, 0);
        try
        {
            _ = FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF);
            FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0, 1);
            var stride = FPDFBitmap_GetStride(bitmap);
            var buffer = (byte*)FPDFBitmap_GetBuffer(bitmap);
            var copy = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
                new ReadOnlySpan<byte>(buffer + ((long)y * stride), width * 4).CopyTo(copy.AsSpan(y * width * 4));
            return Convert.ToHexString(SHA256.HashData(copy))[..12];
        }
        finally
        {
            FPDFBitmap_Destroy(bitmap);
        }
    }

    // fpdfview.h
    [LibraryImport(Library)]
    private static partial float FPDF_GetPageWidthF(nint page);

    [LibraryImport(Library)]
    private static partial float FPDF_GetPageHeightF(nint page);

    [LibraryImport(Library)]
    private static partial nint FPDFBitmap_Create(int width, int height, int alpha);

    [LibraryImport(Library)]
    private static partial void FPDFBitmap_Destroy(nint bitmap);

    [LibraryImport(Library)]
    private static partial nint FPDFBitmap_GetBuffer(nint bitmap);

    [LibraryImport(Library)]
    private static partial int FPDFBitmap_GetStride(nint bitmap);

    [LibraryImport(Library)]
    private static partial int FPDFBitmap_FillRect(nint bitmap, int left, int top, int width, int height, uint color);

    [LibraryImport(Library)]
    private static partial void FPDF_RenderPageBitmap(nint bitmap, nint page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);

    [LibraryImport(Library)]
    private static partial uint FPDF_CountNamedDests(nint document);

    [LibraryImport(Library)]
    private static partial nint FPDF_GetNamedDest(nint document, int index, byte* buffer, int* length);

    // fpdf_doc.h
    [LibraryImport(Library)]
    private static partial nint FPDFBookmark_GetFirstChild(nint document, nint bookmark);

    [LibraryImport(Library)]
    private static partial nint FPDFBookmark_GetNextSibling(nint document, nint bookmark);

    [LibraryImport(Library)]
    private static partial uint FPDFBookmark_GetTitle(nint bookmark, byte* buffer, uint length);

    [LibraryImport(Library)]
    private static partial nint FPDFBookmark_GetDest(nint document, nint bookmark);

    [LibraryImport(Library)]
    private static partial nint FPDFBookmark_GetAction(nint bookmark);

    [LibraryImport(Library)]
    private static partial uint FPDFAction_GetType(nint action);

    [LibraryImport(Library)]
    private static partial nint FPDFAction_GetDest(nint document, nint action);

    [LibraryImport(Library)]
    private static partial int FPDFDest_GetDestPageIndex(nint document, nint dest);

    [LibraryImport(Library)]
    private static partial int FPDFLink_Enumerate(nint page, int* startPos, nint* link);

    [LibraryImport(Library)]
    private static partial nint FPDFLink_GetDest(nint document, nint link);

    [LibraryImport(Library)]
    private static partial nint FPDFLink_GetAction(nint link);

    [LibraryImport(Library)]
    private static partial uint FPDF_GetPageLabel(nint document, int page, byte* buffer, uint length);

    // fpdf_edit.h, fpdf_transformpage.h
    [LibraryImport(Library)]
    private static partial int FPDFPage_GetRotation(nint page);

    [LibraryImport(Library)]
    private static partial int FPDFPage_GetMediaBox(nint page, float* left, float* bottom, float* right, float* top);

    [LibraryImport(Library)]
    private static partial int FPDFPage_GetCropBox(nint page, float* left, float* bottom, float* right, float* top);

    // fpdf_text.h
    [LibraryImport(Library)]
    private static partial nint FPDFText_LoadPage(nint page);

    [LibraryImport(Library)]
    private static partial void FPDFText_ClosePage(nint textPage);

    [LibraryImport(Library)]
    private static partial int FPDFText_GetText(nint textPage, int start, int count, ushort* result);

    // fpdf_annot.h
    [LibraryImport(Library)]
    private static partial nint FPDFPage_GetAnnot(nint page, int index);

    [LibraryImport(Library)]
    private static partial void FPDFPage_CloseAnnot(nint annotation);

    [LibraryImport(Library)]
    private static partial int FPDFAnnot_GetSubtype(nint annotation);

    [LibraryImport(Library)]
    private static partial uint FPDFAnnot_GetFormFieldName(nint form, nint annotation, byte* buffer, uint length);

    [LibraryImport(Library)]
    private static partial int FPDFAnnot_GetFormFieldType(nint form, nint annotation);

    [LibraryImport(Library)]
    private static partial uint FPDFAnnot_GetFormFieldValue(nint form, nint annotation, byte* buffer, uint length);

    // fpdf_formfill.h
    [LibraryImport(Library)]
    private static partial nint FPDFDOC_InitFormFillEnvironment(nint document, nint formInfo);

    [LibraryImport(Library)]
    private static partial void FPDFDOC_ExitFormFillEnvironment(nint form);

    [LibraryImport(Library)]
    private static partial void FORM_OnAfterLoadPage(nint page, nint form);

    [LibraryImport(Library)]
    private static partial void FORM_OnBeforeClosePage(nint page, nint form);
}
