using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace S4;

/// <summary>
/// Document opened through FPDF_LoadCustomDocument (callback reads from an in-memory buffer, mirroring the
/// broker-provided handle model) and saved through an FPDF_FILEWRITE callback into a byte buffer.
/// </summary>
public sealed unsafe class PdfDoc : IDisposable
{
    delegate int GetBlockFn(IntPtr param, uint position, IntPtr buf, uint size);
    delegate int WriteBlockFn(IntPtr self, IntPtr data, uint size);

    static bool _init;
    public static void Init() { if (!_init) { P.FPDF_InitLibrary(); _init = true; } }

    readonly byte[] _data;
    readonly GCHandle _pin;
    readonly GetBlockFn _getBlock;            // keep delegate alive
    readonly IntPtr _fileAccess;              // unmanaged FPDF_FILEACCESS
    IntPtr _formInfo;
    public IntPtr Doc, Form;
    public byte[] Original => _data;
    public readonly List<IntPtr> OpenPages = new();

    public static PdfDoc Open(byte[] bytes, bool formEnv, out string error)
    {
        Init();
        var d = new PdfDoc(bytes);
        error = "";
        if (d.Doc == IntPtr.Zero) { error = "FPDF_LoadCustomDocument failed, FPDF_GetLastError=" + P.FPDF_GetLastError(); d.Dispose(); return null!; }
        if (formEnv)
        {
            // FPDF_FORMFILLINFO version 1, all callbacks null (headless; no timers, no UI). 1 KiB zeroed is larger than the struct.
            d._formInfo = Marshal.AllocHGlobal(1024);
            new Span<byte>((void*)d._formInfo, 1024).Clear();
            *(int*)d._formInfo = 1;
            d.Form = P.FPDFDOC_InitFormFillEnvironment(d.Doc, d._formInfo);
            if (d.Form == IntPtr.Zero) error = "FPDFDOC_InitFormFillEnvironment returned NULL";
        }
        return d;
    }

    PdfDoc(byte[] data)
    {
        _data = data;
        _pin = GCHandle.Alloc(data, GCHandleType.Pinned);
        _getBlock = (param, pos, buf, size) =>
        {
            if ((ulong)pos + size > (ulong)_data.Length) return 0;
            Marshal.Copy(_data, (int)pos, buf, (int)size);
            return 1;
        };
        // struct { unsigned long m_FileLen; (pad) int (*m_GetBlock)(...); void* m_Param; }
        _fileAccess = Marshal.AllocHGlobal(24);
        new Span<byte>((void*)_fileAccess, 24).Clear();
        *(uint*)_fileAccess = (uint)data.Length;
        *(IntPtr*)((byte*)_fileAccess + 8) = Marshal.GetFunctionPointerForDelegate(_getBlock);
        *(IntPtr*)((byte*)_fileAccess + 16) = IntPtr.Zero;
        Doc = P.FPDF_LoadCustomDocument(_fileAccess, null);
    }

    public IntPtr LoadPage(int idx)
    {
        var p = P.FPDF_LoadPage(Doc, idx);
        if (p != IntPtr.Zero)
        {
            if (Form != IntPtr.Zero) P.FORM_OnAfterLoadPage(p, Form);
            OpenPages.Add(p);
        }
        return p;
    }

    /// <summary>FPDF_SaveAsCopy into memory through an FPDF_FILEWRITE callback.</summary>
    public byte[]? Save(uint flags, out string error)
    {
        var ms = new MemoryStream();
        WriteBlockFn wb = (self, data, size) =>
        {
            var tmp = new byte[size];
            Marshal.Copy(data, tmp, 0, (int)size);
            ms.Write(tmp, 0, tmp.Length);
            return 1;
        };
        // struct { int version; (pad) int (*WriteBlock)(...); }
        var fw = Marshal.AllocHGlobal(16);
        new Span<byte>((void*)fw, 16).Clear();
        *(int*)fw = 1;
        *(IntPtr*)((byte*)fw + 8) = Marshal.GetFunctionPointerForDelegate(wb);
        try
        {
            if (Form != IntPtr.Zero) P.FORM_ForceToKillFocus(Form);
            int ok = P.FPDF_SaveAsCopy(Doc, fw, flags);
            GC.KeepAlive(wb);
            error = ok != 0 ? "" : "FPDF_SaveAsCopy returned 0";
            return ok != 0 ? ms.ToArray() : null;
        }
        finally { Marshal.FreeHGlobal(fw); }
    }

    public void Dispose()
    {
        foreach (var p in OpenPages)
        {
            if (Form != IntPtr.Zero) P.FORM_OnBeforeClosePage(p, Form);
            P.FPDF_ClosePage(p);
        }
        OpenPages.Clear();
        if (Form != IntPtr.Zero) { P.FPDFDOC_ExitFormFillEnvironment(Form); Form = IntPtr.Zero; }
        if (Doc != IntPtr.Zero) { P.FPDF_CloseDocument(Doc); Doc = IntPtr.Zero; }
        if (_formInfo != IntPtr.Zero) { Marshal.FreeHGlobal(_formInfo); _formInfo = IntPtr.Zero; }
        if (_fileAccess != IntPtr.Zero) Marshal.FreeHGlobal(_fileAccess);
        if (_pin.IsAllocated) _pin.Free();
    }

    // ---------- helpers
    public static string Utf16(byte[] buf, uint len)
    {
        if (len < 2) return "";
        return Encoding.Unicode.GetString(buf, 0, (int)len - 2);
    }

    public static string GetAnnotString(IntPtr annot, string key)
    {
        uint n = P.FPDFAnnot_GetStringValue(annot, key, null, 0);
        if (n <= 2) return "";
        var buf = new byte[n];
        fixed (byte* b = buf) P.FPDFAnnot_GetStringValue(annot, key, b, n);
        return Utf16(buf, n);
    }

    public static string FormFieldName(IntPtr form, IntPtr annot)
    {
        uint n = P.FPDFAnnot_GetFormFieldName(form, annot, null, 0);
        if (n <= 2) return "";
        var buf = new byte[n];
        fixed (byte* b = buf) P.FPDFAnnot_GetFormFieldName(form, annot, b, n);
        return Utf16(buf, n);
    }

    public static string FormFieldValue(IntPtr form, IntPtr annot)
    {
        uint n = P.FPDFAnnot_GetFormFieldValue(form, annot, null, 0);
        if (n <= 2) return "";
        var buf = new byte[n];
        fixed (byte* b = buf) P.FPDFAnnot_GetFormFieldValue(form, annot, b, n);
        return Utf16(buf, n);
    }

    public static string FormFieldExportValue(IntPtr form, IntPtr annot)
    {
        uint n = P.FPDFAnnot_GetFormFieldExportValue(form, annot, null, 0);
        if (n <= 2) return "";
        var buf = new byte[n];
        fixed (byte* b = buf) P.FPDFAnnot_GetFormFieldExportValue(form, annot, b, n);
        return Utf16(buf, n);
    }

    public static FS_RECTF GetRect(IntPtr annot)
    {
        FS_RECTF r = default;
        P.FPDFAnnot_GetRect(annot, &r);
        return r;
    }

    // ---------- rendering (no FFLDraw: annotations only through FPDF_ANNOT, i.e. no regeneration by the form-fill layer)
    public static (int w, int h, byte[] bgra) Render(IntPtr page, double scale, bool annots, IntPtr formForFfl = default)
    {
        int w = (int)Math.Round(P.FPDF_GetPageWidthF(page) * scale), h = (int)Math.Round(P.FPDF_GetPageHeightF(page) * scale);
        var bmp = P.FPDFBitmap_Create(w, h, 0);
        P.FPDFBitmap_FillRect(bmp, 0, 0, w, h, 0xFFFFFFFF);
        P.FPDF_RenderPageBitmap(bmp, page, 0, 0, w, h, 0, annots ? P.FPDF_ANNOT : 0);
        if (formForFfl != IntPtr.Zero) P.FPDF_FFLDraw(formForFfl, bmp, page, 0, 0, w, h, 0, annots ? P.FPDF_ANNOT : 0);
        int stride = P.FPDFBitmap_GetStride(bmp);
        var buf = (byte*)P.FPDFBitmap_GetBuffer(bmp);
        var outp = new byte[w * h * 4];
        for (int y = 0; y < h; y++) Marshal.Copy((IntPtr)(buf + (long)y * stride), outp, y * w * 4, w * 4);
        P.FPDFBitmap_Destroy(bmp);
        return (w, h, outp);
    }

    // ---------- minimal PNG writer (RGB from BGRx)
    public static void WritePng(string path, int w, int h, byte[] bgra)
    {
        var raw = new byte[(w * 3 + 1) * h];
        for (int y = 0; y < h; y++)
        {
            int o = y * (w * 3 + 1);
            raw[o] = 0;
            for (int x = 0; x < w; x++)
            {
                int s = (y * w + x) * 4;
                raw[o + 1 + x * 3] = bgra[s + 2]; raw[o + 2 + x * 3] = bgra[s + 1]; raw[o + 3 + x * 3] = bgra[s];
            }
        }
        using var fs = File.Create(path);
        fs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        BE(ihdr, 0, w); BE(ihdr, 4, h); ihdr[8] = 8; ihdr[9] = 2;
        Chunk(fs, "IHDR", ihdr);
        using var cm = new MemoryStream();
        using (var z = new ZLibStream(cm, CompressionLevel.Fastest, true)) z.Write(raw);
        Chunk(fs, "IDAT", cm.ToArray());
        Chunk(fs, "IEND", Array.Empty<byte>());
    }
    static void BE(byte[] b, int o, int v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }
    static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; BE(len, 0, data.Length); s.Write(len);
        var t = Encoding.ASCII.GetBytes(type); s.Write(t); s.Write(data);
        uint crc = Crc32(t, 0xFFFFFFFF); crc = Crc32(data, crc) ^ 0xFFFFFFFF;
        var c = new byte[4]; BE(c, 0, (int)crc); s.Write(c);
    }
    static uint[]? _crcT;
    static uint Crc32(byte[] d, uint crc)
    {
        if (_crcT == null)
        {
            _crcT = new uint[256];
            for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; _crcT[n] = c; }
        }
        foreach (var b in d) crc = _crcT[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
