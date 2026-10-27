using System.Diagnostics;
using System.Formats.Asn1;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using S8.Core;

namespace S8.Validate;

/// <summary>S8.2: PDF signature validator (byte range, digest, CMS, chain, post-signing modification analysis).</summary>
public sealed class Validator(ValidatorOptions opts)
{
    private sealed class Candidate
    {
        public PdfIndirect Obj = null!;
        public PdfDict Dict = null!;
        public string? FieldName;
        public bool Reachable;
    }

    public FileReport ValidateFile(byte[] data, string name)
    {
        var rep = new FileReport { File = name, Size = data.Length };
        var file = new PdfFile(data);
        PdfView? final = null;
        try { final = file.OpenFinalView(); }
        catch (Exception e) when (e is PdfFormatException or PdfUnsupportedException or ArgumentException or InvalidDataException)
        { rep.StructureError = e.Message; }
        if (final != null)
        {
            rep.Revisions = final.Sections.Count(s => true);
            if (final.IsEncrypted) rep.Notes.Add("File is encrypted: object streams / strings cannot be compared; modification analysis limited.");
        }

        List<Candidate> cands;
        string? walkErr = null;
        try { cands = Locate(file, final, out walkErr); if (walkErr != null) rep.Notes.Add("AcroForm walk failed (" + walkErr + "); signatures were carved from raw bytes and are reported as not reachable"); }
        catch (Exception e) when (e is PdfFormatException or PdfUnsupportedException) { rep.StructureError ??= "signature location failed: " + e.Message; cands = new(); }

        int idx = 0;
        foreach (var c in cands.OrderBy(c => c.Obj.Start))
        {
            var sw = Stopwatch.StartNew();
            var sr = new SignatureReport { Index = idx++, FileLength = data.Length, ReachableFromAcroForm = c.Reachable, FieldName = c.FieldName, ObjectNumber = c.Obj.Num };
            try { ValidateOne(file, final, c, sr); }
            catch (Exception e) when (e is PdfFormatException or PdfUnsupportedException or CryptographicException or ArgumentException or AsnContentException or InvalidOperationException)
            {
                sr.Checks.Add(new Check("Internal", Outcome.Fail, $"{e.GetType().Name}: {e.Message}"));
                sr.Status = SigStatus.Unknown;
                sr.StatusReason = "validation aborted: " + e.Message;
            }
            sr.ValidateMs = sw.Elapsed.TotalMilliseconds;
            rep.Signatures.Add(sr);
        }
        if (rep.Signatures.Count == 0 && final == null) rep.Notes.Add("Structure unparseable and no signature dictionaries could be carved.");
        return rep;
    }

    // ------------------------------------------------------------------ locating signatures

    private static List<Candidate> Locate(PdfFile file, PdfView? final, out string? walkError)
    {
        walkError = null;
        var res = new Dictionary<int, Candidate>(); // by object start offset
        if (final != null)
        {
            try
            {
                var root = final.ResolveDict(final.Trailer["Root"]);
                var acro = root == null ? null : final.ResolveDict(root["AcroForm"]);
                var fields = acro == null ? null : final.ResolveArray(acro["Fields"]);
                var seen = new HashSet<int>();
                void Walk(PdfObj? o, string prefix, int depth)
                {
                    if (depth > 32) return;
                    if (o is PdfArray a) { foreach (var x in a.Items) Walk(x, prefix, depth + 1); return; }
                    if (o is not PdfRef r || !seen.Add(r.Num)) return;
                    var fi = final.GetIndirect(r.Num);
                    var d = final.ResolveDict(r);
                    if (d == null || fi == null) return;
                    string nm = prefix;
                    if (final.Resolve(d["T"]) is PdfString ts) nm = prefix.Length == 0 ? ts.ToString() : prefix + "." + ts;
                    string? ft = null;
                    var cur = d;
                    for (int i = 0; i < 16 && cur != null && ft == null; i++) { ft = cur["FT"].NameOf(); cur = final.ResolveDict(cur["Parent"]); }
                    if (ft == "Sig" && d["V"] != null)
                    {
                        PdfIndirect? vo = d["V"] is PdfRef vr ? final.GetIndirect(vr.Num) : null;
                        if (vo != null && final.Resolve(d["V"]) is PdfDict vd)
                            res[vo.Start] = new Candidate { Obj = vo, Dict = vd, FieldName = nm, Reachable = true };
                        else if (final.Resolve(d["V"]) is PdfDict vd2)
                            res[fi.Start] = new Candidate { Obj = fi, Dict = vd2, FieldName = nm, Reachable = true };
                    }
                    Walk(final.Resolve(d["Kids"]), nm, depth + 1);
                }
                Walk(fields, "", 0);
            }
            catch (Exception e) when (e is PdfFormatException or PdfUnsupportedException)
            {
                walkError = e.Message; // AcroForm walk failed: fall back to carving below; whatever it finds is 'not reachable'
            }
        }
        // carving: every object in the raw file that carries a /ByteRange (finds unreachable / superseded signature dictionaries)
        var text = System.Text.Encoding.Latin1.GetString(file.Data);
        var objRx = new Regex(@"(?<![0-9])(\d{1,10})[ \t\r\n]+(\d{1,5})[ \t\r\n]+obj\b", RegexOptions.Compiled);
        var objStarts = objRx.Matches(text).Select(m => m.Index).ToList();
        foreach (Match m in Regex.Matches(text, "/ByteRange"))
        {
            int pos = objStarts.LastOrDefault(s => s < m.Index, -1);
            if (pos < 0) continue;
            if (res.ContainsKey(pos)) continue;
            try
            {
                var ind = file.P.ParseIndirectAt(pos);
                if (ind.End < m.Index) continue;
                if (ind.Obj is PdfDict d && d.Has("ByteRange")) res[pos] = new Candidate { Obj = ind, Dict = d, Reachable = false };
            }
            catch (PdfFormatException) { /* not a parseable object: handled as note below */ }
        }
        return res.Values.ToList();
    }

    // ------------------------------------------------------------------ one signature

    private void ValidateOne(PdfFile file, PdfView? final, Candidate c, SignatureReport sr)
    {
        var data = file.Data;
        var d = c.Dict;
        void Chk(string id, Outcome o, string detail) => sr.Checks.Add(new Check(id, o, detail));

        sr.SubFilter = d["SubFilter"].NameOf();
        sr.IsDocTimeStamp = d["Type"].NameOf() == "DocTimeStamp";
        if (!c.Reachable) Chk("Reachability", Outcome.Warn, "signature dictionary is not reachable from /AcroForm /Fields of the final document (superseded, removed or hidden)");
        else Chk("Reachability", Outcome.Pass, $"field '{c.FieldName}'");

        // ---- fields
        if (d.Duplicates.Count > 0)
        {
            Chk("Structure", Outcome.Fail, $"signature dictionary has duplicate key(s) {string.Join(", ", d.Duplicates.Select(k => "/" + k))}: first-wins and last-wins readers would disagree");
            sr.Integrity = "Broken";
            Finish(sr, SigStatus.Invalid, "ambiguous signature dictionary (duplicate keys)");
            return;
        }
        var br = d["ByteRange"] as PdfArray;
        var contents = d["Contents"] as PdfString;
        if (br == null || br.Count != 4 || br.Items.Any(x => x is not PdfInt) || contents == null)
        {
            Chk("Structure", Outcome.Fail, "/ByteRange (4 integers) and /Contents (string) are required");
            Finish(sr, SigStatus.Invalid, "incomplete signature dictionary (unsigned placeholder or malformed)");
            sr.Integrity = "Broken";
            return;
        }
        var b = br.Items.Select(x => ((PdfInt)x).Value).ToArray();
        sr.ByteRange = b.Select(x => (int)Math.Clamp(x, int.MinValue, int.MaxValue)).ToArray();
        sr.ContentsCapacityBytes = contents.Value.Length;
        if (d["Reason"] is PdfString rs) sr.Reason = rs.ToString();
        if (d["M"] is PdfString ms) sr.ClaimedSigningTime = ms.ToString();

        // ---- (a) byte range coverage
        bool cov = true;
        string covDetail = "";
        void CovFail(string m) { cov = false; covDetail += (covDetail.Length > 0 ? "; " : "") + m; }
        long len = data.Length;
        if (b.Any(x => x < 0)) CovFail("negative value in /ByteRange");
        if (b[0] != 0) CovFail($"first range starts at {b[0]}, not 0");
        if (b[1] > b[2]) CovFail("ranges overlap or are out of order");
        if (b[2] + b[3] > len) CovFail($"second range ends at {b[2] + b[3]} beyond file length {len}");
        if (!contents.IsHex) CovFail("/Contents is a literal string, not a hex string");
        if (cov)
        {
            if (contents.Start != b[1] || contents.End != b[2])
                CovFail($"gap [{b[1]},{b[2]}) is not exactly the /Contents token [{contents.Start},{contents.End}) (unsigned bytes inside the gap)");
            if (contents.Start < c.Obj.Start || contents.End > c.Obj.End) CovFail("/Contents token not inside its signature object");
        }
        long signedEnd = (long)b[2] + b[3];
        sr.SignedRevisionEnd = (int)Math.Clamp(signedEnd, 0, int.MaxValue);
        PdfView? oldView = null;
        if (cov)
        {
            // revision boundary: signed range must end exactly at %%EOF (plus whitespace)
            int e = (int)signedEnd;
            int t = e;
            while (t > 0 && PdfParser.IsWs(data[t - 1])) t--;
            if (!(t >= 5 && file.P.StartsWith(t - 5, "%%EOF"))) CovFail($"signed range ends at {signedEnd}, which is not the end of a revision (no %%EOF): xref/trailer not covered");
            else
            {
                try
                {
                    oldView = file.OpenView(e);
                    var top = oldView.Sections[0];
                    if (top.EofEnd != t) CovFail("signed revision's cross-reference section does not end at the signed range end");
                    else
                    {
                        var ent = oldView.Entries.GetValueOrDefault(c.Obj.Num);
                        if (ent.Type != 1 || ent.A != c.Obj.Start) CovFail($"signature object {c.Obj.Num} is not defined by the signed revision's xref");
                    }
                }
                catch (PdfFormatException ex) { CovFail("signed revision is not a well-formed revision: " + ex.Message); }
            }
        }
        if (cov && signedEnd < len)
        {
            int e = (int)signedEnd;
            bool onlyWs = true;
            for (int i = e; i < data.Length; i++) if (!PdfParser.IsWs(data[i])) { onlyWs = false; break; }
            if (onlyWs) Chk("TrailingBytes", Outcome.Info, $"{len - signedEnd} whitespace byte(s) after the signed range");
        }
        if (cov) Chk("ByteRangeCoverage", Outcome.Pass, $"[{b[0]} {b[1]} {b[2]} {b[3]}] = hash of everything except the /Contents token; signed revision ends at {signedEnd}, file length {len}");
        else Chk("ByteRangeCoverage", Outcome.Fail, covDetail);

        // ---- subfilter support
        bool detached = sr.SubFilter is "adbe.pkcs7.detached" or "ETSI.CAdES.detached";
        if (sr.IsDocTimeStamp || sr.SubFilter == "ETSI.RFC3161")
        {
            Chk("SubFilter", Outcome.Info, "document timestamp (ETSI.RFC3161): enumerated only, not validated in this spike");
            if (!cov) { Finish(sr, SigStatus.Invalid, "byte range invalid"); sr.Integrity = "Broken"; return; }
            Finish(sr, SigStatus.Unknown, "document timestamps are not validated in this spike");
            return;
        }
        if (!detached) Chk("SubFilter", Outcome.Warn, $"SubFilter '{sr.SubFilter}' is not adbe.pkcs7.detached / ETSI.CAdES.detached (legacy/unsupported)");
        else Chk("SubFilter", Outcome.Pass, sr.SubFilter!);

        // ---- DocMDP
        if (oldView != null) sr.DocMdpPermission = ReadDocMdp(oldView);

        if (!cov)
        {
            sr.Integrity = "Broken";
            Finish(sr, SigStatus.Invalid, "byte range does not cover the document correctly: " + covDetail);
            return;
        }

        // ---- (b)+(c) digest and CMS
        var signedBytes = new byte[b[1] + b[3]];
        Buffer.BlockCopy(data, (int)b[0], signedBytes, 0, (int)b[1]);
        Buffer.BlockCopy(data, (int)b[2], signedBytes, (int)b[1], (int)b[3]);
        var cmsOk = CheckCms(contents.Value, signedBytes, sr, out var signer, out var cmsCerts, out var signingTime, detached);
        sr.Integrity = cmsOk ? "Intact" : "Broken";

        // ---- (d) chain
        if (signer != null) CheckChain(signer, cmsCerts, signingTime, sr);
        else if (cmsOk) { sr.Identity = "Untrusted"; Chk("SignerCertificate", Outcome.Fail, "no signer certificate found in the CMS"); }

        // ---- (e) post-signing modifications
        if (final != null && oldView != null)
            Modifications(file, final, oldView, sr);
        else sr.Modifications = "Unanalysable";

        Decide(sr, detached);
    }

    private static int? ReadDocMdp(PdfView v)
    {
        try
        {
            var root = v.ResolveDict(v.Trailer["Root"]);
            var perms = root == null ? null : v.ResolveDict(root["Perms"]);
            var sig = perms == null ? null : v.ResolveDict(perms["DocMDP"]);
            var refs = sig == null ? null : v.ResolveArray(sig["Reference"]);
            if (refs == null) return null;
            foreach (var r in refs.Items)
            {
                var rd = v.ResolveDict(r);
                if (rd?["TransformMethod"].NameOf() != "DocMDP") continue;
                var tp = v.ResolveDict(rd["TransformParams"]);
                return (int)(tp?["P"].IntOf() ?? 2);
            }
        }
        catch (Exception e) when (e is PdfFormatException or PdfUnsupportedException) { }
        return null;
    }

    // ------------------------------------------------------------------ CMS

    private static HashAlgorithmName? HashFor(string? oid) => oid switch
    {
        "1.3.14.3.2.26" => HashAlgorithmName.SHA1,
        "2.16.840.1.101.3.4.2.1" => HashAlgorithmName.SHA256,
        "2.16.840.1.101.3.4.2.2" => HashAlgorithmName.SHA384,
        "2.16.840.1.101.3.4.2.3" => HashAlgorithmName.SHA512,
        "1.2.840.113549.2.5" => HashAlgorithmName.MD5,
        _ => null
    };

    private bool CheckCms(byte[] contentsRaw, byte[] signedBytes, SignatureReport sr, out X509Certificate2? signer, out X509Certificate2Collection certs, out DateTimeOffset? signingTime, bool detachedSubFilter)
    {
        void Chk(string id, Outcome o, string detail) => sr.Checks.Add(new Check(id, o, detail));
        signer = null; certs = new X509Certificate2Collection(); signingTime = null;

        // strip /Contents zero padding using the ASN.1 length of the outer structure
        int cmsLen;
        try
        {
            var rdr = new AsnReader(contentsRaw, AsnEncodingRules.BER);
            cmsLen = rdr.PeekEncodedValue().Length;
        }
        catch (AsnContentException ex) { Chk("CmsParse", Outcome.Fail, "/Contents is not a BER structure (unsigned placeholder or garbage): " + ex.Message); return false; }
        sr.CmsLengthBytes = cmsLen;
        bool nonZeroPad = false;
        for (int i = cmsLen; i < contentsRaw.Length; i++) if (contentsRaw[i] != 0) { nonZeroPad = true; break; }
        if (nonZeroPad) Chk("ContentsPadding", Outcome.Warn, "non-zero bytes follow the CMS structure inside /Contents (not covered by the signature)");
        var cmsBytes = contentsRaw.AsSpan(0, cmsLen).ToArray();

        // attached content check (USF-style: validator verifying embedded content instead of the PDF)
        SignedCms probe = new();
        try { probe.Decode(cmsBytes); }
        catch (CryptographicException ex) { Chk("CmsParse", Outcome.Fail, "not a valid CMS SignedData: " + ex.Message); return false; }
        if (probe.ContentInfo.Content.Length != 0 && detachedSubFilter)
        {
            Chk("CmsDetached", Outcome.Fail, $"CMS carries {probe.ContentInfo.Content.Length} bytes of encapsulated content but SubFilter is detached; the PDF bytes would not be what is signed");
            return false;
        }

        var cms = new SignedCms(new ContentInfo(signedBytes), detached: true);
        try { cms.Decode(cmsBytes); }
        catch (CryptographicException ex) { Chk("CmsParse", Outcome.Fail, "CMS decode failed: " + ex.Message); return false; }
        certs = cms.Certificates;
        Chk("CmsParse", Outcome.Pass, $"SignedData, {cms.SignerInfos.Count} signer(s), {cms.Certificates.Count} embedded certificate(s), {cmsLen} bytes of {contentsRaw.Length} reserved");
        if (cms.SignerInfos.Count != 1) { Chk("CmsSigners", Outcome.Fail, $"expected exactly 1 SignerInfo, found {cms.SignerInfos.Count}"); return false; }
        var si = cms.SignerInfos[0];
        signer = si.Certificate;
        sr.DigestAlgorithm = si.DigestAlgorithm.FriendlyName ?? si.DigestAlgorithm.Value;
        try { sr.SignatureAlgorithm = si.SignatureAlgorithm.FriendlyName ?? si.SignatureAlgorithm.Value; } catch { /* property availability varies */ }

        // (b) digest comparison, done independently of CheckSignature so the failure mode is specific
        var hn = HashFor(si.DigestAlgorithm.Value);
        byte[]? md = null;
        foreach (var attr in si.SignedAttributes)
        {
            if (attr.Oid.Value == "1.2.840.113549.1.9.4" && attr.Values.Count > 0)
            { var p = new Pkcs9MessageDigest(); p.CopyFrom(attr.Values[0]); md = p.MessageDigest; }
            if (attr.Oid.Value == "1.2.840.113549.1.9.5" && attr.Values.Count > 0)
            { var p = new Pkcs9SigningTime(); p.CopyFrom(attr.Values[0]); signingTime = p.SigningTime; }
        }
        bool digestOk = true;
        if (hn == null) { Chk("Digest", Outcome.Fail, $"unsupported digest algorithm {si.DigestAlgorithm.Value}"); digestOk = false; }
        else if (md == null) Chk("Digest", Outcome.Info, "no signedAttrs/messageDigest; signature is directly over the content");
        else
        {
            var comp = IncrementalHashOf(hn.Value, signedBytes);
            if (comp.AsSpan().SequenceEqual(md)) Chk("Digest", Outcome.Pass, $"{hn.Value.Name} over {signedBytes.Length} signed bytes equals the CMS messageDigest");
            else { Chk("Digest", Outcome.Fail, $"{hn.Value.Name} over the byte range does not match messageDigest (document bytes changed after signing)"); digestOk = false; }
        }
        if (hn == HashAlgorithmName.SHA1) Chk("DigestStrength", Outcome.Warn, "SHA-1 digest");
        if (hn == HashAlgorithmName.MD5) { Chk("DigestStrength", Outcome.Fail, "MD5 digest"); digestOk = false; }

        // (c) CMS signature (verifies messageDigest again + signature over signedAttrs)
        bool sigOk = false;
        try { cms.CheckSignature(verifySignatureOnly: true); sigOk = true; Chk("CmsSignature", Outcome.Pass, "SignedCms.CheckSignature(verifySignatureOnly: true) succeeded"); }
        catch (CryptographicException ex) { Chk("CmsSignature", Outcome.Fail, "SignedCms.CheckSignature failed: " + ex.Message); }

        if (signer == null) return digestOk && sigOk;
        sr.SignerSubject = signer.Subject; sr.IssuerSubject = signer.Issuer; sr.SignerThumbprint = signer.Thumbprint;
        EssBinding(si, signer, sr, sr.SubFilter == "ETSI.CAdES.detached");
        var ku = signer.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault();
        if (ku != null && (ku.KeyUsages & (X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation)) == 0)
            Chk("KeyUsage", Outcome.Warn, $"signer certificate key usage is {ku.KeyUsages}, lacking digitalSignature/nonRepudiation");
        int bits = 0;
        using (var rsa = signer.GetRSAPublicKey()) bits = rsa?.KeySize ?? 0;
        if (bits > 0 && bits < 2048) Chk("KeyStrength", Outcome.Warn, $"RSA key is {bits} bits");
        return digestOk && sigOk;
    }

    private static byte[] IncrementalHashOf(HashAlgorithmName n, byte[] d)
    {
        using var h = IncrementalHash.CreateHash(n);
        h.AppendData(d);
        return h.GetHashAndReset();
    }

    /// <summary>PAdES / CAdES signing-certificate-v2 (and v1) binding: the signed attribute must name the signer certificate.</summary>
    private static void EssBinding(SignerInfo si, X509Certificate2 signer, SignatureReport sr, bool cades)
    {
        void Chk(string id, Outcome o, string detail) => sr.Checks.Add(new Check(id, o, detail));
        byte[]? v2 = null, v1 = null;
        foreach (var a in si.SignedAttributes)
        {
            if (a.Oid.Value == "1.2.840.113549.1.9.16.2.47" && a.Values.Count > 0) v2 = a.Values[0].RawData;
            if (a.Oid.Value == "1.2.840.113549.1.9.16.2.12" && a.Values.Count > 0) v1 = a.Values[0].RawData;
        }
        if (v2 == null && v1 == null)
        {
            Chk("SigningCertificateAttr", cades ? Outcome.Warn : Outcome.Info, "no signing-certificate / signing-certificate-v2 signed attribute (signer certificate is not cryptographically bound)");
            return;
        }
        try
        {
            var r = new AsnReader(v2 ?? v1!, AsnEncodingRules.BER).ReadSequence();
            var certs = r.ReadSequence();
            var id = certs.ReadSequence();
            HashAlgorithmName hn = HashAlgorithmName.SHA256;
            if (v2 != null)
            {
                if (id.PeekTag().TagValue == 16) // SEQUENCE: explicit AlgorithmIdentifier (default is SHA-256)
                {
                    var alg = id.ReadSequence();
                    hn = HashFor(alg.ReadObjectIdentifier()) ?? throw new CryptographicException("unknown ESS hash alg");
                }
            }
            else hn = HashAlgorithmName.SHA1;
            var certHash = id.ReadOctetString();
            var comp = IncrementalHashOf(hn, signer.RawData);
            if (comp.AsSpan().SequenceEqual(certHash)) Chk("SigningCertificateAttr", Outcome.Pass, $"signing-certificate{(v2 != null ? "-v2" : "")} ({hn.Name}) matches the signer certificate");
            else Chk("SigningCertificateAttr", Outcome.Fail, "signing-certificate hash does not match the signer certificate (certificate substitution)");
        }
        catch (Exception ex) when (ex is AsnContentException or CryptographicException or InvalidOperationException)
        {
            Chk("SigningCertificateAttr", Outcome.Warn, "could not parse signing-certificate attribute: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------ chain

    private void CheckChain(X509Certificate2 signer, X509Certificate2Collection embedded, DateTimeOffset? signingTime, SignatureReport sr)
    {
        void Chk(string id, Outcome o, string detail) => sr.Checks.Add(new Check(id, o, detail));
        X509Chain Make(DateTime when)
        {
            var ch = new X509Chain();
            ch.ChainPolicy.RevocationMode = opts.RevocationMode;
            ch.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
            ch.ChainPolicy.DisableCertificateDownloads = opts.DisableCertificateDownloads;
            ch.ChainPolicy.VerificationTime = when;
            ch.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(3);
            if (!opts.UseSystemTrust)
            {
                ch.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                ch.ChainPolicy.CustomTrustStore.AddRange(opts.TrustAnchors);
            }
            ch.ChainPolicy.ExtraStore.AddRange(embedded);
            return ch;
        }
        var now = opts.VerificationTime ?? DateTime.Now;
        using var chain = Make(now);
        bool ok = chain.Build(signer);
        var flags = chain.ChainStatus.Aggregate(X509ChainStatusFlags.NoError, (a, s) => a | s.Status);
        string path = string.Join(" -> ", chain.ChainElements.Select(e => e.Certificate.GetNameInfo(X509NameType.SimpleName, false)));
        bool hasRevPtr = chain.ChainElements.Cast<X509ChainElement>().Take(chain.ChainElements.Count - 1)
            .Any(e => e.Certificate.Extensions.Any(x => x.Oid?.Value is "2.5.29.31" or "1.3.6.1.5.5.7.1.1"));

        const X509ChainStatusFlags RevOnly = X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation;
        if (ok || flags == X509ChainStatusFlags.NoError || (flags & ~RevOnly) == 0)
        {
            sr.Identity = "Trusted";
            Chk("Chain", Outcome.Pass, $"{path} (trust: {(opts.UseSystemTrust ? "Windows system roots (read-only)" : "custom root store")}; verification time {now:u})");
        }
        else if ((flags & X509ChainStatusFlags.Revoked) != 0)
        {
            sr.Identity = "Revoked"; Chk("Chain", Outcome.Fail, $"{path}: REVOKED");
        }
        else if ((flags & X509ChainStatusFlags.NotTimeValid) != 0)
        {
            // try again at the claimed signing time
            DateTime? when = signingTime?.UtcDateTime ?? ParsePdfDate(sr.ClaimedSigningTime);
            bool recovered = false;
            if (when != null)
            {
                using var c2 = Make(when.Value);
                bool ok2 = c2.Build(signer);
                var f2 = c2.ChainStatus.Aggregate(X509ChainStatusFlags.NoError, (a, s) => a | s.Status);
                recovered = ok2 || f2 == X509ChainStatusFlags.NoError;
            }
            if (recovered) { sr.Identity = "Trusted"; Chk("Chain", Outcome.Warn, $"{path}: a certificate has expired since the claimed signing time; chain was valid at {when:u} (claimed, not backed by a trusted timestamp)"); }
            else { sr.Identity = "Expired"; Chk("Chain", Outcome.Fail, $"{path}: certificate not time-valid ({flags})"); }
        }
        else if ((flags & (X509ChainStatusFlags.UntrustedRoot | X509ChainStatusFlags.PartialChain)) != 0 && (flags & ~(X509ChainStatusFlags.UntrustedRoot | X509ChainStatusFlags.PartialChain | X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation)) == 0)
        {
            sr.Identity = "Untrusted"; Chk("Chain", Outcome.Warn, $"{path}: signer is not anchored in the trust store ({flags}); integrity is intact but the identity is unverified");
        }
        else { sr.Identity = "Untrusted"; Chk("Chain", Outcome.Fail, $"{path}: chain invalid ({flags})"); }

        // revocation
        if (opts.RevocationMode == X509RevocationMode.NoCheck)
            Chk("Revocation", hasRevPtr ? Outcome.Warn : Outcome.Info, hasRevPtr ? "revocation NOT checked (mode NoCheck) although the certificates carry CRL/OCSP pointers" : "revocation not checked; certificates carry no CRL/OCSP pointers");
        else
            Chk("Revocation", (flags & (X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation)) != 0 ? Outcome.Warn : Outcome.Pass, $"mode {opts.RevocationMode}; flags {flags}");
        Chk("Timestamp", Outcome.Info, "no RFC 3161 signature timestamp validated (B-B only)");
    }

    private static DateTime? ParsePdfDate(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        var m = Regex.Match(s, @"^D:(\d{4})(\d{2})?(\d{2})?(\d{2})?(\d{2})?(\d{2})?");
        if (!m.Success) return null;
        int G(int i, int def) => m.Groups[i].Success ? int.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture) : def;
        try { return new DateTime(G(1, 1970), G(2, 1), G(3, 1), G(4, 0), G(5, 0), G(6, 0), DateTimeKind.Utc); } catch { return null; }
    }

    // ------------------------------------------------------------------ modifications

    private void Modifications(PdfFile file, PdfView final, PdfView oldView, SignatureReport sr)
    {
        void Chk(string id, Outcome o, string detail) => sr.Checks.Add(new Check(id, o, detail));
        int e = sr.SignedRevisionEnd;
        // is the signed revision the whole file (modulo trailing whitespace)?
        bool tailWs = true;
        for (int i = e; i < file.Data.Length; i++) if (!PdfParser.IsWs(file.Data[i])) { tailWs = false; break; }
        if (tailWs) { sr.Modifications = "None"; Chk("Modifications", Outcome.Pass, "signed revision is the whole file"); return; }

        // is there a later revision (a startxref after the signed end)?
        var lastSx = file.FindStartXref(file.Data.Length);
        bool laterRevision = lastSx != null && lastSx.Value.kwPos >= e;
        if (!laterRevision)
        {
            sr.Modifications = "AllowedOnly";
            Chk("TrailingBytes", Outcome.Warn, $"{file.Data.Length - e} unsigned byte(s) follow the signed revision but contain no cross-reference section; readers ignore them, but they are not covered");
            sr.Changes.Add(new Change(ChangeClass.AllowedWithWarning, $"{file.Data.Length - e} trailing bytes without a revision (ignored by readers)"));
            return;
        }
        try
        {
            if (final.IsEncrypted && (final.Entries.Values.Any(x => x.Type == 2)))
                throw new PdfUnsupportedException("encrypted file with object streams");
            var an = new ModificationAnalyzer(oldView, final, sr.DocMdpPermission);
            var changes = an.Analyze();
            sr.Changes.AddRange(changes);
            if (changes.Any(x => x.Class == ChangeClass.Disallowed)) { sr.Modifications = "Disallowed"; Chk("Modifications", Outcome.Fail, $"{changes.Count(x => x.Class == ChangeClass.Disallowed)} disallowed change(s) after signing"); }
            else if (changes.Count == 0) { sr.Modifications = "None"; Chk("Modifications", Outcome.Info, "later revision(s) present but they make no semantic change"); }
            else
            {
                sr.Modifications = "AllowedOnly";
                bool warn = changes.Any(x => x.Class == ChangeClass.AllowedWithWarning);
                Chk("Modifications", warn ? Outcome.Warn : Outcome.Pass, $"{changes.Count} change(s) after signing, all permitted{(sr.DocMdpPermission is int p ? $" by DocMDP P={p}" : " (no DocMDP restriction)")}");
            }
        }
        catch (Exception ex) when (ex is PdfFormatException or PdfUnsupportedException or ArgumentException or InvalidDataException)
        {
            sr.Modifications = "Unanalysable";
            Chk("Modifications", Outcome.Fail, "later revision(s) exist but could not be analysed: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------ verdict

    private static void Finish(SignatureReport sr, SigStatus s, string why) { sr.Status = s; sr.StatusReason = why; }

    private static void Decide(SignatureReport sr, bool detachedSubFilter)
    {
        var fails = sr.Checks.Where(c => c.Outcome == Outcome.Fail).ToList();
        if (sr.Integrity == "Broken" || sr.Checks.Any(c => c.Outcome == Outcome.Fail && c.Id is "ByteRangeCoverage" or "Digest" or "CmsSignature" or "CmsParse" or "CmsDetached" or "CmsSigners" or "SigningCertificateAttr"))
        { Finish(sr, SigStatus.Invalid, "integrity: " + string.Join("; ", fails.Select(f => f.Id + ": " + f.Detail))); return; }
        if (sr.Modifications == "Disallowed")
        { Finish(sr, SigStatus.Invalid, "document modified after signing in ways not permitted: " + string.Join("; ", sr.Changes.Where(c => c.Class == ChangeClass.Disallowed).Select(c => c.Description))); return; }
        if (sr.Modifications == "Unanalysable")
        { Finish(sr, SigStatus.Unknown, "signature is intact for the signed revision but later revisions could not be analysed"); return; }
        if (!detachedSubFilter)
        { Finish(sr, SigStatus.Unknown, $"SubFilter {sr.SubFilter} not supported by this validator"); return; }
        if (!sr.ReachableFromAcroForm)
        { Finish(sr, SigStatus.Unknown, "signature dictionary is not part of the document as a reader shows it (superseded, removed or hidden); cryptographically it is intact"); return; }
        if (sr.Identity is "Revoked" or "Expired")
        { Finish(sr, SigStatus.Invalid, $"signer certificate {sr.Identity.ToLowerInvariant()}"); return; }
        if (sr.Identity != "Trusted")
        { Finish(sr, SigStatus.Unknown, "document integrity intact, but the signer's identity is not trusted"); return; }
        var warns = sr.Checks.Where(c => c.Outcome == Outcome.Warn).Select(c => c.Id + ": " + c.Detail).ToList();
        warns.AddRange(sr.Changes.Where(c => c.Class == ChangeClass.AllowedWithWarning).Select(c => "change: " + c.Description));
        if (warns.Count > 0) Finish(sr, SigStatus.ValidWithWarnings, string.Join("; ", warns));
        else Finish(sr, SigStatus.Valid, "integrity intact, signer trusted, no modifications after signing" + (sr.Changes.Count > 0 ? " other than permitted signing" : ""));
    }
}
