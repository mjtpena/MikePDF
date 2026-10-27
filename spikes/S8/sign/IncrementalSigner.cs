using System.Diagnostics;
using System.Formats.Asn1;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using S8.Core;

namespace S8.Sign;

public sealed class SignOptions
{
    public required X509Certificate2 Signer { get; init; }
    public X509Certificate2Collection ExtraCertificates { get; } = new();
    /// <summary>Optional separate private key (e.g. a non-exportable CNG key that cannot be attached to the certificate object).</summary>
    public AsymmetricAlgorithm? SigningKey { get; init; }
    /// <summary>"ETSI.CAdES.detached" (PAdES B-B) or "adbe.pkcs7.detached".</summary>
    public string SubFilter { get; init; } = "ETSI.CAdES.detached";
    public HashAlgorithmName Digest { get; init; } = HashAlgorithmName.SHA256;
    public int ContentsCapacityBytes { get; init; } = 16 * 1024;
    public string FieldName { get; init; } = "Signature1";
    public string? Name { get; init; }
    public string? Reason { get; init; }
    public string? Location { get; init; }
    public string? ContactInfo { get; init; }
    public DateTimeOffset SigningTime { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>Certification signature (DocMDP) permission 1..3, or null for an approval signature.</summary>
    public int? DocMdp { get; init; }
    public bool Visible { get; init; }
    public double[] Rect { get; init; } = { 50, 50, 300, 110 };
    public int PageIndex { get; init; }
    public bool IncludeSigningCertificateV2 { get; init; } = true;
    /// <summary>Test hook: raw text appended inside the signature dictionary (e.g. to create duplicate keys).</summary>
    public string? ExtraSignatureDictText { get; init; }
    /// <summary>Test hook: false = sign a range that stops before the new xref section (a classic signer bug).</summary>
    public bool CoverToEndOfFile { get; init; } = true;
}

public sealed class SignResult
{
    public byte[] Pdf { get; init; } = Array.Empty<byte>();
    public byte[] UnsignedPlaceholderPdf { get; init; } = Array.Empty<byte>();
    public int CmsLength { get; init; }
    public int Capacity { get; init; }
    public int[] ByteRange { get; init; } = Array.Empty<int>();
    public double BuildMs { get; init; }
    public double HashMs { get; init; }
    public double CmsMs { get; init; }
    public double PatchMs { get; init; }
    public double TotalMs { get; init; }
    public bool UsedXrefStream { get; init; }
    public int SigObjNum { get; init; }
    public int FieldObjNum { get; init; }
}

/// <summary>
/// S8.3: appends an incremental update that adds a signature field, widget and /V dictionary with a /Contents placeholder,
/// then patches /ByteRange and /Contents in place. The embryo of Quire.PdfWriter's signing path.
/// </summary>
public static class IncrementalSigner
{
    public static SignResult Sign(byte[] pdf, SignOptions o)
    {
        var total = Stopwatch.StartNew();
        var t0 = Stopwatch.StartNew();
        var file = new PdfFile(pdf);
        var view = file.OpenFinalView();
        if (view.IsEncrypted) throw new NotSupportedException("encrypted input is out of scope for the spike");
        var rootRef = view.RootRef ?? throw new PdfFormatException("no /Root");
        var catalog = view.ResolveDict(rootRef) ?? throw new PdfFormatException("catalog unreadable");

        // ---- locate the target page
        var (pageRef, pageDict) = FindPage(view, catalog["Pages"], o.PageIndex);

        // ---- object numbers
        int maxExisting = Math.Max(view.Size, view.Entries.Keys.DefaultIfEmpty(0).Max() + 1);
        int sigNum = maxExisting, fieldNum = maxExisting + 1, nextNum = maxExisting + 2;
        int apNum = o.Visible ? nextNum++ : 0;
        bool xrefStream = view.Sections[0].IsStream;
        int xrefNum = xrefStream ? nextNum++ : 0;
        int newSize = nextNum;

        // ---- rewritten existing objects (catalog / AcroForm / Fields / page / Annots)
        var rewritten = new SortedDictionary<int, string>(); // object number -> serialised body
        int GenOf(int num) => view.Entries.TryGetValue(num, out var e) && e.Type == 1 ? e.B : 0;
        var fieldRef = new PdfRef(fieldNum, 0);

        // page /Annots
        var newPage = pageDict.Clone();
        var annotsObj = pageDict["Annots"];
        if (annotsObj is PdfRef annotsRef && view.Resolve(annotsRef) is PdfArray ra)
        {
            var na = new PdfArray(ra.Items) ; na.Items.Add(fieldRef);
            rewritten[annotsRef.Num] = PdfSerializer.ToPdf(na);
        }
        else
        {
            var na = annotsObj is PdfArray da ? new PdfArray(da.Items) : new PdfArray();
            na.Items.Add(fieldRef);
            newPage["Annots"] = na;
        }
        rewritten[pageRef.Num] = PdfSerializer.ToPdf(newPage);

        // catalog /AcroForm
        var newCat = catalog.Clone();
        var af = catalog["AcroForm"];
        PdfDict afDict;
        int? afNum = null;
        if (af is PdfRef afRef && view.ResolveDict(afRef) is PdfDict afd) { afDict = afd.Clone(); afNum = afRef.Num; }
        else if (af is PdfDict afdirect) afDict = afdirect.Clone();
        else afDict = new PdfDict();
        var fieldsObj = afDict["Fields"];
        if (fieldsObj is PdfRef fieldsRef && view.Resolve(fieldsRef) is PdfArray fa)
        {
            var nf = new PdfArray(fa.Items); nf.Items.Add(fieldRef);
            rewritten[fieldsRef.Num] = PdfSerializer.ToPdf(nf);
        }
        else
        {
            var nf = fieldsObj is PdfArray fda ? new PdfArray(fda.Items) : new PdfArray();
            nf.Items.Add(fieldRef);
            afDict["Fields"] = nf;
        }
        afDict["SigFlags"] = new PdfInt(3);
        if (afNum != null) rewritten[afNum.Value] = PdfSerializer.ToPdf(afDict);
        else newCat["AcroForm"] = afDict;
        if (o.DocMdp is int p)
        {
            if (p is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(o.DocMdp));
            var perms = new PdfDict();
            if (catalog["Perms"] is PdfDict ep) perms = ep.Clone();
            perms["DocMDP"] = new PdfRef(sigNum, 0);
            newCat["Perms"] = perms;
        }
        rewritten[rootRef.Num] = PdfSerializer.ToPdf(newCat);

        // ---- assemble
        var ms = new MemoryStream(pdf.Length + 32768 + o.ContentsCapacityBytes * 2);
        ms.Write(pdf);
        if (pdf.Length == 0 || (pdf[^1] != 10 && pdf[^1] != 13)) ms.WriteByte(10);
        var offsets = new SortedDictionary<int, (long off, int gen)>();
        void WriteAscii(string s) { var b = Encoding.Latin1.GetBytes(s); ms.Write(b); }

        foreach (var (num, body) in rewritten)
        {
            offsets[num] = (ms.Position, GenOf(num));
            WriteAscii($"{num} {GenOf(num)} obj\n{body}\nendobj\n");
        }

        // appearance stream
        if (o.Visible)
        {
            double w = o.Rect[2] - o.Rect[0], h = o.Rect[3] - o.Rect[1];
            var sbc = new StringBuilder();
            sbc.Append(FormattableString.Invariant($"q 0.93 g 0 0 {w} {h} re f Q 0.4 G 0.5 w 0.25 0.25 {w - 0.5} {h - 0.5} re S\n"));
            sbc.Append(FormattableString.Invariant($"BT /F1 9 Tf 0 g 4 {h - 13} Td ({Esc("Digitally signed by " + (o.Name ?? o.Signer.GetNameInfo(X509NameType.SimpleName, false)))}) Tj 0 -11 Td ({Esc("Date: " + o.SigningTime.ToUniversalTime().ToString("yyyy.MM.dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture))}) Tj"));
            if (o.Reason != null) sbc.Append(FormattableString.Invariant($" 0 -11 Td ({Esc("Reason: " + o.Reason)}) Tj"));
            sbc.Append(" ET\n");
            var content = Encoding.Latin1.GetBytes(sbc.ToString());
            offsets[apNum] = (ms.Position, 0);
            WriteAscii(FormattableString.Invariant($"{apNum} 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 {w} {h}] /Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >> >> >> /Length {content.Length} >>\nstream\n"));
            ms.Write(content);
            WriteAscii("\nendstream\nendobj\n");
        }

        // widget / field
        {
            offsets[fieldNum] = (ms.Position, 0);
            var rect = o.Visible ? FormattableString.Invariant($"[{o.Rect[0]} {o.Rect[1]} {o.Rect[2]} {o.Rect[3]}]") : "[0 0 0 0]";
            WriteAscii($"{fieldNum} 0 obj\n<< /Type /Annot /Subtype /Widget /FT /Sig /T {Text(o.FieldName)} /V {sigNum} 0 R /F 132 /P {pageRef.Num} {pageRef.Gen} R /Rect {rect}{(o.Visible ? $" /AP << /N {apNum} 0 R >>" : "")} >>\nendobj\n");
        }

        // signature dictionary with placeholders
        int brPos, contentsStart, contentsEnd;
        {
            offsets[sigNum] = (ms.Position, 0);
            WriteAscii($"{sigNum} 0 obj\n<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /{o.SubFilter}\n/ByteRange [");
            brPos = (int)ms.Position;
            WriteAscii("0 " + new string(' ', 10) + " " + new string(' ', 10) + " " + new string(' ', 10) + "]\n/Contents ");
            contentsStart = (int)ms.Position;
            ms.WriteByte((byte)'<');
            ms.Write(Encoding.ASCII.GetBytes(new string('0', o.ContentsCapacityBytes * 2)));
            ms.WriteByte((byte)'>');
            contentsEnd = (int)ms.Position;
            var sb = new StringBuilder("\n");
            sb.Append($"/M ({PdfDate(o.SigningTime)})");
            if (o.Name != null) sb.Append($" /Name {Text(o.Name)}");
            if (o.Reason != null) sb.Append($" /Reason {Text(o.Reason)}");
            if (o.Location != null) sb.Append($" /Location {Text(o.Location)}");
            if (o.ContactInfo != null) sb.Append($" /ContactInfo {Text(o.ContactInfo)}");
            if (o.DocMdp is int pm)
                sb.Append($"\n/Reference [<< /Type /SigRef /TransformMethod /DocMDP /TransformParams << /Type /TransformParams /P {pm} /V /1.2 >> >>]");
            if (o.ExtraSignatureDictText != null) sb.Append('\n').Append(o.ExtraSignatureDictText);
            sb.Append("\n>>\nendobj\n");
            WriteAscii(sb.ToString());
        }

        // new cross-reference section
        long xrefOffset = ms.Position;
        var idObj = view.Trailer["ID"];
        var infoObj = view.Trailer["Info"];
        if (!xrefStream)
        {
            var allNums = offsets.Keys.ToList();
            WriteAscii("xref\n");
            int i = 0;
            while (i < allNums.Count)
            {
                int j = i;
                while (j + 1 < allNums.Count && allNums[j + 1] == allNums[j] + 1) j++;
                WriteAscii($"{allNums[i]} {j - i + 1}\n");
                for (int k = i; k <= j; k++) { var (off, gen) = offsets[allNums[k]]; WriteAscii($"{off:D10} {gen:D5} n \n"); }
                i = j + 1;
            }
            var tr = new StringBuilder($"trailer\n<< /Size {newSize} /Root {rootRef.Num} {rootRef.Gen} R /Prev {view.StartXref}");
            if (infoObj != null) tr.Append(" /Info ").Append(PdfSerializer.ToPdf(infoObj));
            if (idObj != null) tr.Append(" /ID ").Append(PdfSerializer.ToPdf(idObj));
            tr.Append(" >>\n");
            WriteAscii(tr.ToString());
        }
        else
        {
            offsets[xrefNum] = (xrefOffset, 0);
            var allNums = offsets.Keys.ToList();
            var idx = new StringBuilder();
            var data = new MemoryStream();
            int i = 0;
            while (i < allNums.Count)
            {
                int j = i;
                while (j + 1 < allNums.Count && allNums[j + 1] == allNums[j] + 1) j++;
                idx.Append($"{allNums[i]} {j - i + 1} ");
                for (int k = i; k <= j; k++)
                {
                    var (off, gen) = offsets[allNums[k]];
                    data.WriteByte(1);
                    data.WriteByte((byte)(off >> 24)); data.WriteByte((byte)(off >> 16)); data.WriteByte((byte)(off >> 8)); data.WriteByte((byte)off);
                    data.WriteByte((byte)(gen >> 8)); data.WriteByte((byte)gen);
                }
                i = j + 1;
            }
            var d = new StringBuilder($"{xrefNum} 0 obj\n<< /Type /XRef /Size {newSize} /W [1 4 2] /Index [{idx.ToString().TrimEnd()}] /Root {rootRef.Num} {rootRef.Gen} R /Prev {view.StartXref}");
            if (infoObj != null) d.Append(" /Info ").Append(PdfSerializer.ToPdf(infoObj));
            if (idObj != null) d.Append(" /ID ").Append(PdfSerializer.ToPdf(idObj));
            d.Append($" /Length {data.Length} >>\nstream\n");
            WriteAscii(d.ToString());
            ms.Write(data.ToArray());
            WriteAscii("\nendstream\nendobj\n");
        }
        WriteAscii($"startxref\n{xrefOffset}\n%%EOF\n");
        double buildMs = t0.Elapsed.TotalMilliseconds;

        var buf = ms.ToArray();
        var placeholderCopy = (byte[])buf.Clone();

        // ---- ByteRange
        var tp = Stopwatch.StartNew();
        int signedEnd = o.CoverToEndOfFile ? buf.Length : (int)xrefOffset;
        int[] br = { 0, contentsStart, contentsEnd, signedEnd - contentsEnd };
        var brText = $"0 {br[1],-10} {br[2],-10} {br[3],-10}";
        Encoding.ASCII.GetBytes(brText).CopyTo(buf, brPos);

        // ---- digest + CMS
        var th = Stopwatch.StartNew();
        var signedBytes = new byte[br[1] + br[3]];
        Buffer.BlockCopy(buf, 0, signedBytes, 0, br[1]);
        Buffer.BlockCopy(buf, br[2], signedBytes, br[1], br[3]);
        byte[] precomputedHash;
        using (var h = IncrementalHash.CreateHash(o.Digest)) { h.AppendData(signedBytes); precomputedHash = h.GetHashAndReset(); }
        double hashMs = th.Elapsed.TotalMilliseconds;

        var tc = Stopwatch.StartNew();
        var cmsBytes = BuildCms(signedBytes, o);
        double cmsMs = tc.Elapsed.TotalMilliseconds;
        if (cmsBytes.Length > o.ContentsCapacityBytes)
            throw new InvalidOperationException($"CMS ({cmsBytes.Length} bytes) does not fit the /Contents placeholder ({o.ContentsCapacityBytes} bytes)");
        var hex = Convert.ToHexString(cmsBytes);
        Encoding.ASCII.GetBytes(hex).CopyTo(buf, contentsStart + 1);
        double patchMs = tp.Elapsed.TotalMilliseconds - hashMs - cmsMs;

        return new SignResult
        {
            Pdf = buf, UnsignedPlaceholderPdf = placeholderCopy, CmsLength = cmsBytes.Length, Capacity = o.ContentsCapacityBytes, ByteRange = br,
            BuildMs = buildMs, HashMs = hashMs, CmsMs = cmsMs, PatchMs = patchMs, TotalMs = total.Elapsed.TotalMilliseconds,
            UsedXrefStream = xrefStream, SigObjNum = sigNum, FieldObjNum = fieldNum
        };
    }

    /// <summary>Detached CMS over the signed bytes; CAdES adds signing-certificate-v2, adbe.pkcs7.detached adds signingTime.</summary>
    public static byte[] BuildCms(byte[] signedBytes, SignOptions o)
    {
        var cms = new SignedCms(new ContentInfo(signedBytes), detached: true);
        var signer = o.SigningKey != null
            ? new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, o.Signer, o.SigningKey)
            : new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, o.Signer);
        signer.DigestAlgorithm = new Oid(DigestOid(o.Digest));
        signer.IncludeOption = X509IncludeOption.EndCertOnly;
        foreach (var c in o.ExtraCertificates) signer.Certificates.Add(c);
        if (o.SubFilter == "ETSI.CAdES.detached" && o.IncludeSigningCertificateV2)
            signer.SignedAttributes.Add(new AsnEncodedData(new Oid("1.2.840.113549.1.9.16.2.47"), SigningCertificateV2(o.Signer, o.Digest)));
        if (o.SubFilter == "adbe.pkcs7.detached")
            signer.SignedAttributes.Add(new Pkcs9SigningTime(o.SigningTime.UtcDateTime));
        cms.ComputeSignature(signer, silent: true); // silent: never show a PIN / UI prompt
        return cms.Encode();
    }

    public static string DigestOid(HashAlgorithmName n) =>
        n == HashAlgorithmName.SHA256 ? "2.16.840.1.101.3.4.2.1" :
        n == HashAlgorithmName.SHA384 ? "2.16.840.1.101.3.4.2.2" :
        n == HashAlgorithmName.SHA512 ? "2.16.840.1.101.3.4.2.3" :
        n == HashAlgorithmName.SHA1 ? "1.3.14.3.2.26" : throw new NotSupportedException(n.Name);

    /// <summary>DER of SigningCertificateV2 { certs { ESSCertIDv2 { [hashAlg], certHash, issuerSerial } } }.</summary>
    public static byte[] SigningCertificateV2(X509Certificate2 cert, HashAlgorithmName hn)
    {
        var hash = cert.GetCertHash(hn);
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        using (w.PushSequence())
        using (w.PushSequence())
        {
            if (hn != HashAlgorithmName.SHA256)
                using (w.PushSequence()) { w.WriteObjectIdentifier(DigestOid(hn)); w.WriteNull(); }
            w.WriteOctetString(hash);
            using (w.PushSequence())            // IssuerSerial
            {
                using (w.PushSequence())        // GeneralNames
                using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 4, true))) // directoryName [4] EXPLICIT
                    w.WriteEncodedValue(cert.IssuerName.RawData);
                w.WriteInteger(new BigInteger(cert.SerialNumberBytes.Span, isUnsigned: true, isBigEndian: true));
            }
        }
        return w.Encode();
    }

    private static (PdfRef, PdfDict) FindPage(PdfView v, PdfObj? node, int index)
    {
        int seen = 0;
        (PdfRef, PdfDict)? Walk(PdfObj? n, int depth)
        {
            if (depth > 40 || n is not PdfRef r) return null;
            var d = v.ResolveDict(r);
            if (d == null) return null;
            if (d.Has("Kids"))
            {
                var kids = v.ResolveArray(d["Kids"]);
                if (kids == null) return null;
                foreach (var k in kids.Items) { var f = Walk(k, depth + 1); if (f != null) return f; }
                return null;
            }
            if (seen++ == index) return (r, d);
            return null;
        }
        return Walk(node, 0) ?? throw new PdfFormatException($"page {index} not found");
    }

    // ---- text helpers
    private static string Esc(string s) => s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

    /// <summary>PDF text string: ASCII literal, else UTF-16BE with BOM as a hex string.</summary>
    public static string Text(string s)
    {
        if (s.All(c => c >= 32 && c < 127)) return "(" + Esc(s) + ")";
        var b = Encoding.BigEndianUnicode.GetBytes(s);
        return "<FEFF" + Convert.ToHexString(b) + ">";
    }

    private static string PdfDate(DateTimeOffset t)
    {
        var u = t.ToUniversalTime();
        return "D:" + u.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "+00'00'";
    }
}
