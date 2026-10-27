using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace S8.Fixtures;

/// <summary>
/// Q2 of S8: how does X509Chain (CustomRootTrust) behave for revocation? Everything stays on the loopback interface:
/// a tiny TcpListener on 127.0.0.1 serves a CRL signed by an in-memory test CA. No external host is contacted.
/// Windows caches fetched CRLs in its per-user CryptnetUrlCache (automatic OS behaviour; no certificate store is modified).
/// OCSP is NOT measured (no responder was built).
/// </summary>
public static class RevocationProbe
{
    sealed class CrlServer : IDisposable
    {
        readonly TcpListener _l = new(IPAddress.Loopback, 0);
        public int Port => ((IPEndPoint)_l.LocalEndpoint).Port;
        public int Hits;
        public byte[] Crl = Array.Empty<byte>();
        readonly CancellationTokenSource _cts = new();
        public CrlServer() { _l.Start(); _ = Task.Run(Loop); }
        async Task Loop()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    using var c = await _l.AcceptTcpClientAsync(_cts.Token);
                    using var s = c.GetStream();
                    var buf = new byte[4096]; int n = await s.ReadAsync(buf, _cts.Token);
                    Interlocked.Increment(ref Hits);
                    var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/pkix-crl\r\nContent-Length: {Crl.Length}\r\nConnection: close\r\n\r\n");
                    await s.WriteAsync(head); await s.WriteAsync(Crl);
                }
            }
            catch (Exception) { }
        }
        public void Dispose() { _cts.Cancel(); _l.Stop(); }
    }

    record Row(string Scenario, string Mode, bool BuildReturned, string Flags, int CrlHits, double Ms);

    public static void Run(string outDir)
    {
        var rows = new List<Row>();
        using var rootKey = RSA.Create(2048);
        var rootReq = new CertificateRequest("CN=Quire S8 revocation-probe root", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        rootReq.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootReq.PublicKey, false));
        using var root = rootReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-5), DateTimeOffset.UtcNow.AddYears(1));

        X509Certificate2 Leaf(string cn, string? cdp, out byte[] serial)
        {
            using var k = RSA.Create(2048);
            var rq = new CertificateRequest($"CN={cn}", k, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            rq.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            rq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            rq.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(root, true, false));
            if (cdp != null) rq.CertificateExtensions.Add(CertificateRevocationListBuilder.BuildCrlDistributionPointExtension(new[] { cdp }));
            serial = new byte[8]; RandomNumberGenerator.Fill(serial); serial[0] &= 0x7F; serial[0] |= 1;
            return rq.Create(root, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(100), serial);
        }
        byte[] MakeCrl(IEnumerable<byte[]> revoked, long number)
        {
            var b = new CertificateRevocationListBuilder();
            foreach (var s in revoked) b.AddEntry(s, DateTimeOffset.UtcNow.AddHours(-1), X509RevocationReason.KeyCompromise);
            return b.Build(root, number, DateTimeOffset.UtcNow.AddDays(2), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1, DateTimeOffset.UtcNow.AddMinutes(-5));
        }

        Row Probe(string name, X509Certificate2 leaf, X509RevocationMode mode, CrlServer? srv, bool disableDownloads = false)
        {
            using var ch = new X509Chain();
            ch.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            ch.ChainPolicy.CustomTrustStore.Add(X509CertificateLoader.LoadCertificate(root.Export(X509ContentType.Cert)));
            ch.ChainPolicy.RevocationMode = mode;
            ch.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
            ch.ChainPolicy.DisableCertificateDownloads = disableDownloads;
            ch.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(5);
            int before = srv?.Hits ?? 0;
            var sw = Stopwatch.StartNew();
            bool ok = ch.Build(leaf);
            var ms = sw.Elapsed.TotalMilliseconds;
            var flags = ch.ChainStatus.Length == 0 ? "NoError" : string.Join("|", ch.ChainStatus.Select(s => s.Status));
            var row = new Row(name, mode + (disableDownloads ? " (downloads disabled)" : ""), ok, flags, (srv?.Hits ?? 0) - before, Math.Round(ms, 1));
            rows.Add(row);
            Console.WriteLine($"{row.Scenario,-52} {row.Mode,-34} build={ok,-5} flags={flags,-40} crlHits={row.CrlHits} {row.Ms} ms");
            return row;
        }

        // scenario 1: leaf without any revocation pointer
        var noCdp = Leaf("probe-no-cdp", null, out _);
        Probe("leaf WITHOUT CDP/OCSP", noCdp, X509RevocationMode.NoCheck, null);
        Probe("leaf WITHOUT CDP/OCSP", noCdp, X509RevocationMode.Offline, null);
        Probe("leaf WITHOUT CDP/OCSP", noCdp, X509RevocationMode.Online, null);

        // scenario 2: CDP points to a closed loopback port (server down)
        var dead = new TcpListener(IPAddress.Loopback, 0); dead.Start(); int deadPort = ((IPEndPoint)dead.LocalEndpoint).Port; dead.Stop();
        var deadLeaf = Leaf("probe-cdp-server-down", $"http://127.0.0.1:{deadPort}/ca.crl", out _);
        Probe("CDP set, server DOWN, no cache", deadLeaf, X509RevocationMode.Offline, null);
        Probe("CDP set, server DOWN, no cache", deadLeaf, X509RevocationMode.Online, null);

        // scenario 3: CDP up, good CRL
        using (var srv = new CrlServer())
        {
            var good = Leaf("probe-cdp-good", $"http://127.0.0.1:{srv.Port}/good.crl", out var serial);
            srv.Crl = MakeCrl(Array.Empty<byte[]>(), 1);
            Probe("CDP set, server up, leaf NOT revoked", good, X509RevocationMode.NoCheck, srv);
            Probe("CDP set, server up, leaf NOT revoked", good, X509RevocationMode.Online, srv);
            Probe("CDP set, server up, leaf NOT revoked (2nd call)", good, X509RevocationMode.Online, srv);
            Probe("CDP set, server up, leaf NOT revoked, cache warm", good, X509RevocationMode.Offline, srv);
            Probe("CDP set, server up, downloads DISABLED", Leaf("probe-cdp-nodl", $"http://127.0.0.1:{srv.Port}/nodl.crl", out _), X509RevocationMode.Online, srv, disableDownloads: true);
        }
        // scenario 4: revoked leaf
        using (var srv = new CrlServer())
        {
            var bad = Leaf("probe-cdp-revoked", $"http://127.0.0.1:{srv.Port}/revoked.crl", out var serial);
            srv.Crl = MakeCrl(new[] { serial }, 1);
            Probe("CDP set, server up, leaf REVOKED", bad, X509RevocationMode.Online, srv);
        }
        // scenario 5: chain to the REAL Windows trust store, using the Authenticode signer certificate of the local dotnet.exe
        // (read-only; NoCheck / Offline; certificate downloads disabled so nothing leaves the machine)
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
            using var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            foreach (var mode in new[] { X509RevocationMode.NoCheck, X509RevocationMode.Offline })
            {
                using var ch = new X509Chain();
                ch.ChainPolicy.RevocationMode = mode; ch.ChainPolicy.DisableCertificateDownloads = true;
                ch.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.3")); // code signing
                var sw = Stopwatch.StartNew(); bool ok = ch.Build(signer);
                var flags = ch.ChainStatus.Length == 0 ? "NoError" : string.Join("|", ch.ChainStatus.Select(x => x.Status));
                var chainNames = string.Join(" -> ", ch.ChainElements.Select(e => e.Certificate.GetNameInfo(X509NameType.SimpleName, false)));
                var row = new Row("REAL Windows trust store (Authenticode cert of dotnet.exe): " + chainNames, mode.ToString(), ok, flags, 0, Math.Round(sw.Elapsed.TotalMilliseconds, 1));
                rows.Add(row);
                Console.WriteLine($"{row.Scenario} | {row.Mode} build={ok} flags={flags} {row.Ms} ms");
            }
        }
        catch (Exception e) { Console.WriteLine("system-store scenario skipped: " + e.GetType().Name + ": " + e.Message); }
        File.WriteAllText(Path.Combine(outDir, "revocation.json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
    }
}
