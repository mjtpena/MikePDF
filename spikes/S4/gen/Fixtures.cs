// Raw PDF fixture writer. The base document, the AcroForm fixture and the signed variants are authored here
// (classic xref, no object streams) so object numbers are known and the incremental "Quire-authored" writer
// (RawAnnots.cs) can append to them without a PDF parser.
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace S4;

public sealed class PdfFile
{
    public byte[] Bytes = Array.Empty<byte>();
    public int XrefOffset;                 // offset of the last "xref" keyword
    public int Size;                       // /Size of the last trailer
    public string Root = "1 0 R";
    public string Info = "";
    public string Id = "";
    public Dictionary<int, byte[]> Bodies = new();   // latest body bytes per object (between "obj\n" and "\nendobj")
    public int SigFieldObj;                // for signed fixtures
    public int[] SigByteRange = Array.Empty<int>();
    public byte[]? SigCms;
}

public static class Fx
{
    public static readonly Encoding L1 = Encoding.Latin1;
    public const string ModDate = "D:20261027120000Z";

    public static byte[] Stream(string dictEntries, string content)
    {
        var c = L1.GetBytes(content);
        var head = L1.GetBytes($"<< {dictEntries} /Length {c.Length} >>\nstream\n");
        var tail = L1.GetBytes("\nendstream");
        return head.Concat(c).Concat(tail).ToArray();
    }
    public static byte[] Obj(string body) => L1.GetBytes(body);

    static string F(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Write a full file from the object table.</summary>
    static PdfFile Write(SortedDictionary<int, byte[]> objs, string root, string info, string id)
    {
        var ms = new MemoryStream();
        void W(string s) { var b = L1.GetBytes(s); ms.Write(b, 0, b.Length); }
        W("%PDF-1.7\n%âãÏÓ\n");
        var offs = new Dictionary<int, int>();
        foreach (var (n, body) in objs)
        {
            offs[n] = (int)ms.Length;
            W($"{n} 0 obj\n"); ms.Write(body); W("\nendobj\n");
        }
        int size = objs.Keys.Max() + 1;
        int xref = (int)ms.Length;
        W($"xref\n0 {size}\n");
        W("0000000000 65535 f \n");
        for (int i = 1; i < size; i++)
            W(offs.TryGetValue(i, out var o) ? $"{o:D10} 00000 n \n" : "0000000000 00000 f \n");
        W($"trailer\n<< /Size {size} /Root {root} {(info != "" ? "/Info " + info : "")} /ID [{id} {id}] >>\nstartxref\n{xref}\n%%EOF\n");
        return new PdfFile
        {
            Bytes = ms.ToArray(), XrefOffset = xref, Size = size, Root = root, Info = info, Id = id,
            Bodies = objs.ToDictionary(k => k.Key, v => v.Value)
        };
    }

    const string IdHex = "<5155495245533453415645464944454C>";

    // Phrases the markup annotations anchor to (verified against FPDFText in the generator).
    public const string PhraseHL = "quick brown fox", PhraseUL = "liquor jugs", PhraseSO = "strike this out", PhraseSQ = "squiggly words";

    public static string PageBody(string annots, string extra = "")
        => $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R /F2 6 0 R /Helv 5 0 R >> >> /Contents 4 0 R{extra}{annots} >>";

    /// <summary>Base document: one page, real text. Optionally with an unsigned signature field (object 8).</summary>
    public static PdfFile BuildBase(bool sigField)
    {
        var o = new SortedDictionary<int, byte[]>();
        string annots = sigField ? " /Annots [8 0 R]" : "";
        o[1] = Obj(sigField ? "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [8 0 R] /SigFlags 3 >> >>" : "<< /Type /Catalog /Pages 2 0 R >>");
        o[2] = Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        o[3] = Obj(PageBody(annots));
        o[4] = Stream("",
            "BT /F2 20 Tf 72 720 Td (Quire Save Fidelity Fixture) Tj ET\n" +
            "BT /F1 12 Tf 72 690 Td (Line A: The quick brown fox jumps over the lazy dog near the river.) Tj ET\n" +
            "BT /F1 12 Tf 72 672 Td (Line B: Pack my box with five dozen liquor jugs and keep this phrase marked.) Tj ET\n" +
            "BT /F1 12 Tf 72 654 Td (Line C: Please strike this out because it is no longer correct in this draft.) Tj ET\n" +
            "BT /F1 12 Tf 72 636 Td (Line D: Some squiggly words here mark a spelling concern for the reviewer.) Tj ET\n" +
            "BT /F1 12 Tf 72 618 Td (Line E: Notes, stamps and shapes go in the empty area below this paragraph.) Tj ET\n" +
            "0.8 g 72 100 468 1 re f 0 g\n" +
            "BT /F1 9 Tf 72 86 Td (End of fixture page 1. Generated for spike S4.) Tj ET\n");
        o[5] = Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        o[6] = Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
        o[7] = Obj($"<< /Title (S4 base fixture) /Producer (Quire S4 spike) /CreationDate ({ModDate}) >>");
        if (sigField) o[8] = Obj("<< /Type /Annot /Subtype /Widget /FT /Sig /T (Signature1) /Rect [0 0 0 0] /F 132 /P 3 0 R >>");
        var f = Write(o, "1 0 R", "7 0 R", IdHex);
        f.SigFieldObj = sigField ? 8 : 0;
        return f;
    }

    // ---- AcroForm fixture ----
    public sealed record FormField(string Name, int Type /*P.FT_**/, string Kind, FS_RECTF Rect, string[]? Opts = null, int[]? RadioWidgetIdx = null);

    public static readonly (string name, string kind, float x, float y, float w, float h)[] FormLayout =
    {
        ("txt_single", "text", 150, 700, 250, 22),
        ("txt_multi",  "multiline", 150, 620, 250, 64),
        ("txt_comb",   "comb", 150, 580, 250, 22),
        ("chk",        "checkbox", 150, 540, 20, 20),
        ("radioGroup", "radio", 150, 500, 20, 20),     // first kid; second at x=200
        ("combo_ro",   "combo", 150, 460, 250, 22),
        ("combo_edit", "combo_edit", 150, 420, 250, 22),
        ("list_multi", "list", 150, 320, 250, 80),
        ("push",       "push", 150, 270, 100, 28),
    };
    public static readonly string[] Opts = { "Alpha", "Beta", "Gamma", "Delta", "Epsilon" };

    static string Circle(double cx, double cy, double r)
    {
        double k = 0.5523 * r;
        return $"{F(cx + r)} {F(cy)} m {F(cx + r)} {F(cy + k)} {F(cx + k)} {F(cy + r)} {F(cx)} {F(cy + r)} c " +
               $"{F(cx - k)} {F(cy + r)} {F(cx - r)} {F(cy + k)} {F(cx - r)} {F(cy)} c " +
               $"{F(cx - r)} {F(cy - k)} {F(cx - k)} {F(cy - r)} {F(cx)} {F(cy - r)} c " +
               $"{F(cx + k)} {F(cy - r)} {F(cx + r)} {F(cy - k)} {F(cx + r)} {F(cy)} c h";
    }

    public static PdfFile BuildForms(bool sigField)
    {
        var o = new SortedDictionary<int, byte[]>();
        int next = 10;
        int Alloc() => next++;
        string APres = "/Resources << /Font << /Helv 5 0 R /ZaDb 8 0 R >> >>";
        byte[] BoxAP(double w, double h, string extra = "") =>
            Stream($"/Type /XObject /Subtype /Form /BBox [0 0 {F(w)} {F(h)}] {APres}",
                $"/Tx BMC q 0.93 0.93 1 rg 0 G 0.5 w 0.25 0.25 {F(w - 0.5)} {F(h - 0.5)} re B Q {extra}EMC");

        var widgetRefs = new List<int>();     // page /Annots (widgets)
        var fieldRefs = new List<int>();      // AcroForm /Fields (top-level fields)
        string optArr = "[" + string.Join(" ", Opts.Select(s => $"({s})")) + "]";

        foreach (var (name, kind, x, y, w, h) in FormLayout)
        {
            string rect = $"[{F(x)} {F(y)} {F(x + w)} {F(y + h)}]";
            string common = $"/Type /Annot /Subtype /Widget /T ({name}) /Rect {rect} /P 3 0 R /F 4 /MK << /BC [0] /BG [0.93 0.93 1] >> /DA (/Helv 12 Tf 0 g)";
            int n = Alloc();
            switch (kind)
            {
                case "text":
                case "multiline":
                case "comb":
                {
                    int ap = Alloc();
                    o[ap] = BoxAP(w, h);
                    string ff = kind == "multiline" ? " /Ff 4096" : kind == "comb" ? " /Ff 16777216 /MaxLen 8" : "";
                    o[n] = Obj($"<< {common} /FT /Tx{ff} /AP << /N {ap} 0 R >> >>");
                    widgetRefs.Add(n); fieldRefs.Add(n);
                    break;
                }
                case "checkbox":
                {
                    int on = Alloc(), off = Alloc();
                    o[off] = BoxAP(w, h);
                    o[on] = Stream($"/Type /XObject /Subtype /Form /BBox [0 0 {F(w)} {F(h)}] {APres}",
                        $"/Tx BMC q 0.93 0.93 1 rg 0 G 0.5 w 0.25 0.25 {F(w - 0.5)} {F(h - 0.5)} re B 0 g BT /ZaDb 14 Tf 3 4 Td (4) Tj ET Q EMC");
                    o[n] = Obj($"<< {common.Replace("/DA (/Helv 12 Tf 0 g)", "/DA (/ZaDb 0 Tf 0 g)").Replace("/BG [0.93 0.93 1]", "/BG [0.93 0.93 1] /CA (4)")} /FT /Btn /V /Off /AS /Off /AP << /N << /Yes {on} 0 R /Off {off} 0 R >> >> >>");
                    widgetRefs.Add(n); fieldRefs.Add(n);
                    break;
                }
                case "radio":
                {
                    // parent field + two kids
                    int parent = n;
                    int k1 = Alloc(), k2 = Alloc();
                    int on1 = Alloc(), off1 = Alloc(), on2 = Alloc(), off2 = Alloc();
                    byte[] RadioOff() => Stream($"/Type /XObject /Subtype /Form /BBox [0 0 20 20] {APres}",
                        $"/Tx BMC q 0.93 0.93 1 rg 0 G 0.5 w {Circle(10, 10, 9.5)} B Q EMC");
                    byte[] RadioOn() => Stream($"/Type /XObject /Subtype /Form /BBox [0 0 20 20] {APres}",
                        $"/Tx BMC q 0.93 0.93 1 rg 0 G 0.5 w {Circle(10, 10, 9.5)} B 0 g {Circle(10, 10, 4.5)} f Q EMC");
                    o[off1] = RadioOff(); o[on1] = RadioOn(); o[off2] = RadioOff(); o[on2] = RadioOn();
                    string kidCommon = "/Type /Annot /Subtype /Widget /P 3 0 R /F 4 /MK << /BC [0] /BG [0.93 0.93 1] /CA (l) >> /DA (/ZaDb 0 Tf 0 g)";
                    o[parent] = Obj($"<< /FT /Btn /Ff 49152 /T ({name}) /V /Off /Kids [{k1} 0 R {k2} 0 R] >>");
                    o[k1] = Obj($"<< {kidCommon} /Parent {parent} 0 R /Rect [150 500 170 520] /AS /Off /AP << /N << /ChoiceA {on1} 0 R /Off {off1} 0 R >> >> >>");
                    o[k2] = Obj($"<< {kidCommon} /Parent {parent} 0 R /Rect [200 500 220 520] /AS /Off /AP << /N << /ChoiceB {on2} 0 R /Off {off2} 0 R >> >> >>");
                    widgetRefs.Add(k1); widgetRefs.Add(k2); fieldRefs.Add(parent);
                    break;
                }
                case "combo":
                case "combo_edit":
                {
                    int ap = Alloc();
                    o[ap] = BoxAP(w, h);
                    string ff = kind == "combo" ? "131072" : "393216";
                    o[n] = Obj($"<< {common} /FT /Ch /Ff {ff} /Opt {optArr} /AP << /N {ap} 0 R >> >>");
                    widgetRefs.Add(n); fieldRefs.Add(n);
                    break;
                }
                case "list":
                {
                    int ap = Alloc();
                    o[ap] = BoxAP(w, h);
                    o[n] = Obj($"<< {common} /FT /Ch /Ff 2097152 /Opt {optArr} /AP << /N {ap} 0 R >> >>");
                    widgetRefs.Add(n); fieldRefs.Add(n);
                    break;
                }
                case "push":
                {
                    int ap = Alloc();
                    o[ap] = Stream($"/Type /XObject /Subtype /Form /BBox [0 0 {F(w)} {F(h)}] {APres}",
                        $"q 0.8 g 0 G 1 w 0.5 0.5 {F(w - 1)} {F(h - 1)} re B Q BT /Helv 12 Tf 0 g 28 9 Td (Submit) Tj ET");
                    o[n] = Obj($"<< {common.Replace("/BG [0.93 0.93 1]", "/BG [0.8 0.8 0.8] /CA (Submit)")} /FT /Btn /Ff 65536 /AP << /N {ap} 0 R >> >>");
                    widgetRefs.Add(n); fieldRefs.Add(n);
                    break;
                }
            }
        }

        int sigObj = 0;
        if (sigField)
        {
            sigObj = Alloc();
            o[sigObj] = Obj("<< /Type /Annot /Subtype /Widget /FT /Sig /T (Signature1) /Rect [0 0 0 0] /F 132 /P 3 0 R >>");
            widgetRefs.Add(sigObj); fieldRefs.Add(sigObj);
        }

        string fields = string.Join(" ", fieldRefs.Select(r => $"{r} 0 R"));
        string annots = " /Annots [" + string.Join(" ", widgetRefs.Select(r => $"{r} 0 R")) + "]";
        o[1] = Obj($"<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [{fields}] /DA (/Helv 0 Tf 0 g) /DR << /Font << /Helv 5 0 R /ZaDb 8 0 R >> >>{(sigField ? " /SigFlags 3" : "")} >> >>");
        o[2] = Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        o[3] = Obj(PageBody(annots));
        var sb = new StringBuilder();
        sb.Append("BT /F2 18 Tf 72 740 Td (Quire AcroForm Fixture) Tj ET\n");
        foreach (var (name, kind, x, y, w, h) in FormLayout)
            sb.Append($"BT /F1 10 Tf 72 {F(y + 6)} Td ({name}) Tj ET\n");
        sb.Append("BT /F1 10 Tf 174 506 Td (A) Tj ET BT /F1 10 Tf 224 506 Td (B) Tj ET\n");
        o[4] = Stream("", sb.ToString());
        o[5] = Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        o[6] = Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
        o[7] = Obj($"<< /Title (S4 forms fixture) /Producer (Quire S4 spike) /CreationDate ({ModDate}) >>");
        o[8] = Obj("<< /Type /Font /Subtype /Type1 /BaseFont /ZapfDingbats >>");
        var f = Write(o, "1 0 R", "7 0 R", IdHex);
        f.SigFieldObj = sigObj;
        return f;
    }

    // ---- Incremental update helper (used for signing and for the Quire-authored annotation cases) ----
    public static PdfFile Append(PdfFile prev, SortedDictionary<int, byte[]> changed, int newSize, Func<byte[], byte[]>? postProcess = null)
    {
        var ms = new MemoryStream();
        ms.Write(prev.Bytes);
        void W(string s) { var b = L1.GetBytes(s); ms.Write(b, 0, b.Length); }
        var offs = new Dictionary<int, int>();
        foreach (var (n, body) in changed)
        {
            offs[n] = (int)ms.Length;
            W($"{n} 0 obj\n"); ms.Write(body); W("\nendobj\n");
        }
        int xref = (int)ms.Length;
        W("xref\n");
        // group contiguous object numbers into subsections
        var nums = changed.Keys.OrderBy(x => x).ToList();
        int i = 0;
        while (i < nums.Count)
        {
            int j = i;
            while (j + 1 < nums.Count && nums[j + 1] == nums[j] + 1) j++;
            W($"{nums[i]} {j - i + 1}\n");
            for (int k = i; k <= j; k++) W($"{offs[nums[k]]:D10} 00000 n \n");
            i = j + 1;
        }
        W($"trailer\n<< /Size {Math.Max(newSize, prev.Size)} /Root {prev.Root} {(prev.Info != "" ? "/Info " + prev.Info : "")} /Prev {prev.XrefOffset} /ID [{prev.Id} {prev.Id}] >>\nstartxref\n{xref}\n%%EOF\n");
        var bodies = new Dictionary<int, byte[]>(prev.Bodies);
        foreach (var kv in changed) bodies[kv.Key] = kv.Value;
        var bytes = ms.ToArray();
        if (postProcess != null) bytes = postProcess(bytes);
        return new PdfFile
        {
            Bytes = bytes, XrefOffset = xref, Size = Math.Max(newSize, prev.Size), Root = prev.Root, Info = prev.Info, Id = prev.Id,
            Bodies = bodies, SigFieldObj = prev.SigFieldObj
        };
    }

    // ---- Signing: adbe.pkcs7.detached with a throw-away self-signed RSA-2048 / SHA-256 certificate ----
    public static PdfFile Sign(PdfFile unsigned)
    {
        const int ContentsHexLen = 16384;            // 8 KiB of CMS
        int sigObj = unsigned.Size;                  // new object number
        string br = "[0 0000000000 0000000000 0000000000]";
        string contents = "<" + new string('0', ContentsHexLen) + ">";
        var changed = new SortedDictionary<int, byte[]>();
        changed[sigObj] = Obj($"<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached /ByteRange {br} /Contents {contents} /M ({ModDate}) /Name (S4 Test Signer) /Reason (S4 spike fixture) >>");
        // rewrite the signature field with /V
        string fieldBody = L1.GetString(unsigned.Bodies[unsigned.SigFieldObj]).TrimEnd();
        fieldBody = fieldBody.Substring(0, fieldBody.Length - 2) + $" /V {sigObj} 0 R >>";
        changed[unsigned.SigFieldObj] = Obj(fieldBody);
        var f = Append(unsigned, changed, sigObj + 1);
        var bytes = f.Bytes;
        // locate placeholders in the appended part
        string text = L1.GetString(bytes);
        int brPos = text.LastIndexOf(br, StringComparison.Ordinal);
        int cPos = text.LastIndexOf(contents, StringComparison.Ordinal);
        int cStart = cPos, cEnd = cPos + contents.Length;
        int[] range = { 0, cStart, cEnd, bytes.Length - cEnd };
        var newBr = L1.GetBytes($"[0 {range[1]:D10} {range[2]:D10} {range[3]:D10}]");
        Buffer.BlockCopy(newBr, 0, bytes, brPos, newBr.Length);
        var signed = bytes.Take(cStart).Concat(bytes.Skip(cEnd)).ToArray();

        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=Quire S4 Test Signer, O=Quire Spike (not for production)", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        using var cert0 = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        using var cert = new X509Certificate2(cert0.Export(X509ContentType.Pfx, "x"), "x", X509KeyStorageFlags.Exportable);
        var cms = new SignedCms(new ContentInfo(signed), true);
        cms.ComputeSignature(new CmsSigner(cert) { DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"), IncludeOption = X509IncludeOption.EndCertOnly });
        var der = cms.Encode();
        if (der.Length * 2 > ContentsHexLen) throw new Exception("CMS too large for placeholder");
        var hex = Convert.ToHexString(der).PadRight(ContentsHexLen, '0');
        Buffer.BlockCopy(L1.GetBytes(hex), 0, bytes, cStart + 1, ContentsHexLen);
        f.Bytes = bytes; f.SigByteRange = range; f.SigCms = der;
        return f;
    }

    /// <summary>Verify a detached CMS over the ByteRange of <paramref name="file"/> (ByteRange as given, not re-read).</summary>
    public static (bool ok, string msg) VerifyCms(byte[] file, int[] br, byte[] cmsDer)
    {
        try
        {
            if ((long)br[2] + br[3] > file.Length) return (false, "ByteRange exceeds file length");
            var data = file.Take(br[1]).Concat(file.Skip(br[2]).Take(br[3])).ToArray();
            var cms = new SignedCms(new ContentInfo(data), true);
            cms.Decode(cmsDer);
            cms.CheckSignature(true);
            return (true, "CMS signature verifies over ByteRange (signature-only; chain not evaluated)");
        }
        catch (Exception e) { return (false, "CheckSignature failed: " + e.Message); }
    }
}
