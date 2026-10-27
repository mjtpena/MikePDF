// S8 fixture / bench driver.
//   gen <outDir>            create certs, fixtures, manifest
//   post-pyhanko <outDir>   Quire signs on top of the pyHanko-signed file
//   validate <outDir>       run Quire's validator over every manifest entry -> quire.json
//   bench <outDir>          signing + validation timing (30 samples)
//   cng <outDir>            CmsSigner with non-exportable CNG keys
//   revocation <outDir>     X509Chain revocation behaviour probe (loopback only)
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using S8.Fixtures;
using S8.Sign;
using S8.Validate;

string cmd = args.Length > 0 ? args[0] : "";
string outDir = Path.GetFullPath(args.Length > 1 ? args[1] : "out/fixtures");
var jopts = new JsonSerializerOptions { WriteIndented = true };

switch (cmd)
{
    case "gen": Generator.Run(outDir); break;
    case "post-pyhanko": PostPyHanko(outDir); break;
    case "validate": ValidateAll(outDir); break;
    case "bench": Bench(outDir); break;
    case "cng": CngProbe(outDir); break;
    case "revocation": RevocationProbe.Run(outDir); break;
    case "audit": Audit(outDir); break;
    case "corpus-sign": CorpusSign(outDir, args.Length > 2 ? args[2] : "../_shared/corpus/pdfium-resources"); break;
    default: Console.WriteLine("commands: gen | post-pyhanko | validate | bench | cng | revocation  <outDir>"); return 1;
}
return 0;

static ValidatorOptions OptionsFor(string outDir, IEnumerable<string> trust)
{
    var o = new ValidatorOptions();
    foreach (var t in trust) o.TrustAnchors.Add(X509CertificateLoader.LoadCertificateFromFile(Path.Combine(outDir, "certs", $"root-{t}.cer")));
    return o;
}

void PostPyHanko(string dir)
{
    var src = Path.Combine(dir, "pyhanko-signed.pdf");
    if (!File.Exists(src)) { Console.WriteLine("pyhanko-signed.pdf missing; run crossvalidate/pyhanko_sign.py first"); return; }
    var ec = LoadPfx(Path.Combine(dir, "certs", "leaf-ecdsa-TESTONLY.pfx"));
    var data = File.ReadAllBytes(src);
    var res = IncrementalSigner.Sign(data, new SignOptions { Signer = ec, FieldName = "QuireSig2", Name = "Quire S8 Test Signer", Visible = true, Rect = new double[] { 320, 50, 570, 110 }, Reason = "Countersigned by Quire" });
    File.WriteAllBytes(Path.Combine(dir, "quire-after-pyhanko.pdf"), res.Pdf);
    Console.WriteLine($"quire-after-pyhanko.pdf: {res.Pdf.Length} bytes (base used xref stream: {res.UsedXrefStream})");
}

static X509Certificate2 LoadPfx(string path) => X509CertificateLoader.LoadPkcs12FromFile(path, "test", X509KeyStorageFlags.EphemeralKeySet);

void ValidateAll(string dir)
{
    var specs = JsonSerializer.Deserialize<List<FixtureSpec>>(File.ReadAllText(Path.Combine(dir, "manifest.json")))!;
    var rows = new List<object>();
    int ok = 0, total = 0;
    foreach (var s in specs)
    {
        var path = Path.Combine(dir, s.File);
        if (!File.Exists(path)) { rows.Add(new { s.Id, s.File, Missing = true }); Console.WriteLine($"{s.Id,-34} (file not generated)"); continue; }
        var rep = new Validator(OptionsFor(dir, s.Trust)).ValidateFile(File.ReadAllBytes(path), s.File);
        var statuses = rep.Signatures.Select(x => x.Status.ToString()).ToArray();
        bool match = statuses.SequenceEqual(s.Expected);
        total++; if (match) ok++;
        Console.WriteLine($"{s.Id,-34} {string.Join(",", statuses),-34} expected {string.Join(",", s.Expected),-34} {(match ? "OK" : "MISMATCH")}");
        foreach (var sig in rep.Signatures.Where(_ => !match)) Console.WriteLine("      " + sig.StatusReason);
        rows.Add(new { s.Id, s.File, Missing = false, Expected = s.Expected, Report = rep });
    }
    File.WriteAllText(Path.Combine(dir, "quire.json"), JsonSerializer.Serialize(rows, jopts));
    Console.WriteLine($"\nQuire classified {ok}/{total} fixtures as expected");
}

static (double p50, double p95, double max) Pct(List<double> xs)
{
    var s = xs.OrderBy(x => x).ToList();
    double At(double p) => s[Math.Min(s.Count - 1, (int)Math.Ceiling(p * s.Count) - 1)];
    return (At(0.5), At(0.95), s[^1]);
}

void Bench(string dir)
{
    var bd = Pdf.BaseDocument();
    var report = new List<object>();
    foreach (var (name, pki) in new[] { ("RSA-3072", CertFactory.Rsa3072()), ("ECDSA-P256", CertFactory.EcdsaP256()) })
    {
        var opts = new SignOptions { Signer = pki.Leaf, Visible = true, Name = "bench", Reason = "bench" };
        var runs = new List<SignResult>();
        for (int i = 0; i < 31; i++) runs.Add(IncrementalSigner.Sign(bd, opts));
        var first = runs[0]; var warm = runs.Skip(1).ToList();
        var o = new ValidatorOptions(); o.TrustAnchors.Add(pki.Root);
        var v = new Validator(o);
        var vms = new List<double>();
        byte[] sample = warm[0].Pdf;
        var vfirst = v.ValidateFile(sample, "bench").Signatures[0].ValidateMs;
        for (int i = 0; i < 30; i++) vms.Add(v.ValidateFile(sample, "bench").Signatures[0].ValidateMs);
        object P(Func<SignResult, double> f) { var (a, b, c) = Pct(warm.Select(f).ToList()); return new { p50 = Math.Round(a, 3), p95 = Math.Round(b, 3), max = Math.Round(c, 3) }; }
        var (vp50, vp95, vmax) = Pct(vms);
        var row = new
        {
            key = name, n_warm = warm.Count, pdfBytes = sample.Length, cmsBytes = first.CmsLength, capacity = first.Capacity,
            first_call_in_process_total_ms = Math.Round(first.TotalMs, 3),
            warm_total_ms = P(r => r.TotalMs), warm_build_ms = P(r => r.BuildMs), warm_hash_ms = P(r => r.HashMs), warm_cms_ms = P(r => r.CmsMs),
            validate_first_call_ms = Math.Round(vfirst, 3), validate_warm_ms = new { p50 = Math.Round(vp50, 3), p95 = Math.Round(vp95, 3), max = Math.Round(vmax, 3) }
        };
        report.Add(row);
        Console.WriteLine(JsonSerializer.Serialize(row, jopts));
    }
    var env = new { machine = Environment.MachineName == null ? "" : "DEV box (name withheld)", os = System.Runtime.InteropServices.RuntimeInformation.OSDescription, runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, cores = Environment.ProcessorCount, config = "Release, x64, not pinned to a power plan, plugged-in state not recorded" };
    File.WriteAllText(Path.Combine(dir, "bench.json"), JsonSerializer.Serialize(new { env, report }, jopts));
}

void CngProbe(string dir)
{
    // Non-exportable, ephemeral CNG keys (never persisted, never in a certificate store).
    // CertificateRequest.CreateSelfSigned / CopyWithPrivateKey need to export the key, which a non-exportable key refuses
    // (measured below), so the certificate is issued with an X509SignatureGenerator and CmsSigner gets the key separately.
    var results = new List<object>();
    var bd = Pdf.BaseDocument();
    foreach (var alg in new[] { "RSA-3072", "ECDSA-P256" })
    {
        bool rsa = alg.StartsWith("RSA");
        using CngKey key = rsa
            ? CngKey.Create(CngAlgorithm.Rsa, null, new CngKeyCreationParameters { ExportPolicy = CngExportPolicies.None, Parameters = { new CngProperty("Length", BitConverter.GetBytes(3072), CngPropertyOptions.None) } })
            : CngKey.Create(CngAlgorithm.ECDsaP256, null, new CngKeyCreationParameters { ExportPolicy = CngExportPolicies.None });
        AsymmetricAlgorithm k = rsa ? new RSACng(key) : new ECDsaCng(key);
        bool exportable;
        try { if (rsa) ((RSA)k).ExportParameters(true); else ((ECDsa)k).ExportParameters(true); exportable = true; } catch (CryptographicException) { exportable = false; }
        string createSelfSigned;
        var subj = new X500DistinguishedName($"CN=Quire S8 CNG non-exportable {alg}");
        CertificateRequest req = rsa ? new CertificateRequest(subj, (RSA)k, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1) : new CertificateRequest(subj, (ECDsa)k, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        try { using var c = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30)); createSelfSigned = "succeeded"; }
        catch (CryptographicException e) { createSelfSigned = "failed: " + e.Message; }
        X509SignatureGenerator gen = rsa ? X509SignatureGenerator.CreateForRSA((RSA)k, RSASignaturePadding.Pkcs1) : X509SignatureGenerator.CreateForECDsa((ECDsa)k);
        var serial = new byte[8]; RandomNumberGenerator.Fill(serial); serial[0] &= 0x7F; serial[0] |= 1;
        using var cert = req.Create(subj, gen, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30), serial);
        string outcome;
        try
        {
            var res = IncrementalSigner.Sign(bd, new SignOptions { Signer = cert, SigningKey = k, Visible = true, Name = "cng" });
            var vo = new ValidatorOptions(); vo.TrustAnchors.Add(X509CertificateLoader.LoadCertificate(cert.Export(X509ContentType.Cert)));
            var rep = new Validator(vo).ValidateFile(res.Pdf, "cng.pdf");
            outcome = $"signed {res.Pdf.Length} bytes in {res.TotalMs:F1} ms (CMS {res.CmsMs:F1} ms); Quire verdict {rep.Signatures[0].Status}";
            File.WriteAllBytes(Path.Combine(dir, $"cng-{alg}.pdf"), res.Pdf);
        }
        catch (Exception e) { outcome = "FAILED: " + e.GetType().Name + ": " + e.Message; }
        results.Add(new { alg, keyObjectType = k.GetType().Name, privateKeyExportable = exportable, createSelfSignedWithKey = createSelfSigned, cmsSignerWithSeparateKey = outcome });
        Console.WriteLine($"{alg}: {k.GetType().Name}, exportable={exportable}; CreateSelfSigned {createSelfSigned}; CmsSigner(cert, key): {outcome}");
        k.Dispose();
    }
    File.WriteAllText(Path.Combine(dir, "cng.json"), JsonSerializer.Serialize(results, jopts));
}


void Audit(string dir)
{
    // xref audit: every in-use entry in the final view of every Quire/pyHanko-written fixture must resolve to "N G obj" with the right number
    var specs = JsonSerializer.Deserialize<List<FixtureSpec>>(File.ReadAllText(Path.Combine(dir, "manifest.json")))!;
    int files = 0, objs = 0, sections = 0, errors = 0;
    foreach (var s in specs.Where(x => File.Exists(Path.Combine(dir, x.File))))
    {
        try
        {
            var v = new S8.Core.PdfFile(File.ReadAllBytes(Path.Combine(dir, s.File))).OpenFinalView();
            sections += v.Sections.Count;
            foreach (var n in v.LiveObjectNumbers) { v.GetObject(n); objs++; }
            files++;
        }
        catch (Exception e) { errors++; Console.WriteLine($"{s.File}: {e.Message}"); }
    }
    Console.WriteLine($"xref audit: {files} files, {sections} xref sections, {objs} live objects resolved, {errors} errors");
}

void CorpusSign(string dir, string corpus)
{
    var pki = CertFactory.Rsa3072();
    var o = Path.Combine(dir, "..", "corpus-signed"); Directory.CreateDirectory(o);
    File.WriteAllBytes(Path.Combine(o, "root-rsa.cer"), pki.RootCer);
    var rows = new List<object>();
    int tried = 0, parsed = 0, signed = 0, valid = 0;
    foreach (var f in Directory.EnumerateFiles(corpus, "*.pdf").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
    {
        tried++;
        var data = File.ReadAllBytes(f);
        string outcome; string? verdict = null;
        try
        {
            new S8.Core.PdfFile(data).OpenFinalView(); parsed++;
            var res = IncrementalSigner.Sign(data, new SignOptions { Signer = pki.Leaf, Name = "corpus" });
            signed++;
            var vo = new ValidatorOptions(); vo.TrustAnchors.Add(pki.Root);
            var rep = new Validator(vo).ValidateFile(res.Pdf, "x.pdf");
            verdict = rep.Signatures.Count == 0 ? "NoSignatureFound" : rep.Signatures[^1].Status.ToString();
            if (verdict == "Valid") valid++;
            outcome = "signed";
            File.WriteAllBytes(Path.Combine(o, Path.GetFileName(f)), res.Pdf);
        }
        catch (Exception e) { outcome = e.GetType().Name + ": " + e.Message.ReplaceLineEndings(" "); }
        rows.Add(new { file = Path.GetFileName(f), outcome, verdict });
    }
    Console.WriteLine($"corpus: tried {tried}, parsed {parsed}, signed {signed}, Quire verdict Valid {valid}");
    File.WriteAllText(Path.Combine(dir, "..", "corpus", "sign-results.json"), JsonSerializer.Serialize(rows, jopts));
}
