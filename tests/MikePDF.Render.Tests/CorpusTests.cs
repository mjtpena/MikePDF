using System.Net;
using System.Text;
using MikePDF.Render.Corpus;
using MikePDF.Render.Engine;

namespace MikePDF.Render.Tests;

public sealed class CorpusTests
{
    private static string ManifestPath => Path.Combine(Locations.RepositoryRoot() ?? throw new DirectoryNotFoundException("MikePDF.slnx not found above the tests"), "tests", "corpus", "manifest.json");

    private static CorpusFile Entry(string id = "x/a.pdf", string sha = "", long bytes = 0, string origin = CorpusOrigin.Fetch, string? generator = null, string licence = "BSD-3-Clause", params string[] tags) => new()
    {
        Id = id,
        Origin = origin,
        Source = origin == CorpusOrigin.Fetch ? "https://example.test/a.pdf" : "generated:" + generator,
        Generator = generator,
        Licence = licence,
        Redistributable = true,
        Sha256 = sha.Length == 0 ? new string('a', 64) : sha,
        Bytes = bytes,
        Tags = [.. tags],
        Tiers = ["smoke"],
    };

    private static CorpusManifest Manifest(params CorpusFile[] files) => new() { Files = [.. files] };

    // ---- the checked-in manifest ----------------------------------------------------------------

    [Fact]
    public void TheCheckedInManifestPassesTheCheckCiRuns()
    {
        var manifest = CorpusManifest.Load(ManifestPath);
        Assert.Empty(ManifestValidator.Validate(manifest));
    }

    [Fact]
    public void TheSmokeTierHasAtLeast30FilesCoveringWhatTheGateAsksFor()
    {
        var smoke = CorpusManifest.Load(ManifestPath).Files.Where(f => f.Tiers.Contains("smoke")).ToList();
        Assert.True(smoke.Count >= 30, $"the smoke tier has {smoke.Count} files");
        foreach (var tag in ManifestValidator.SmokeCoverage)
        {
            Assert.Contains(smoke, f => f.Tags.Contains(tag));
        }

        // The brief's list, spelled out: text, vector, images, JBIG2, CCITT, forms, annotations, encrypted, broken xref, huge page counts, rotated pages, CropBox offsets.
        Assert.Contains(smoke, f => f.Tags.Contains("huge-page-count") && f.Expect.Pages >= 10_000);
        Assert.Contains(smoke, f => f.Tags.Contains("encrypted") && f.Expect.Status == "Ok" && f.Expect.Password is not null);
        Assert.Contains(smoke, f => f.Tags.Contains("broken-xref") && f.Expect.Status == "Damaged");
        Assert.Contains(smoke, f => f.Tags.Contains("broken-xref") && f.Expect.Status == "Ok");
    }

    [Fact]
    public void EveryEntryNamesItsLicenceItsHashAndWhereItCameFrom()
    {
        var manifest = CorpusManifest.Load(ManifestPath);
        foreach (var file in manifest.Files)
        {
            Assert.Equal(64, file.Sha256.Length);
            Assert.NotEmpty(file.Licence);
            Assert.True(file.Redistributable, file.Id);
            Assert.NotEmpty(file.Source);
            if (file.Origin == CorpusOrigin.Generated)
            {
                Assert.Equal("Apache-2.0", file.Licence);
                continue;
            }

            // A fetched file comes from a pinned, immutable address, and says where its licence was read.
            Assert.StartsWith("https://", file.Source, StringComparison.Ordinal);
            Assert.Contains(file.Licence, ManifestValidator.AllowedLicences);
            if (file.Id.StartsWith("pdfium/", StringComparison.Ordinal))
            {
                Assert.StartsWith("https://pdfium.googlesource.com/pdfium/+/43d55e429cf72c21e9a1e60475b50feefa16fda9/testing/resources/", file.Source, StringComparison.Ordinal);
                Assert.Equal("BSD-3-Clause", file.Licence);
            }

            if (!file.Tiers.Contains("smoke"))
            {
                Assert.False(string.IsNullOrWhiteSpace(file.LicenceEvidence), file.Id);
            }
            else
            {
                Assert.False(string.IsNullOrWhiteSpace(file.Audit), file.Id + " is in the smoke tier and was audited");
            }
        }

        // No file is fetched from a mutable address: a git branch or a "latest".
        Assert.DoesNotContain(manifest.Files.Where(f => f.Origin == CorpusOrigin.Fetch), f => f.Source.Contains("/blob/main/", StringComparison.Ordinal) || f.Source.Contains("/blob/master/", StringComparison.Ordinal) || (f.FetchUrl ?? "").Contains("/main/", StringComparison.Ordinal) || (f.FetchUrl ?? "").Contains("/master/", StringComparison.Ordinal) || (f.FetchUrl ?? "").Contains("/staging/", StringComparison.Ordinal));

        Assert.NotEmpty(manifest.Excluded);
        Assert.All(manifest.Excluded, e => Assert.NotEmpty(e.Reason));
    }

    [Fact]
    public void EveryGeneratedFileIsReproducedByteForByteAndMatchesItsManifestHash()
    {
        foreach (var file in CorpusManifest.Load(ManifestPath).Files.Where(f => f.Origin == CorpusOrigin.Generated))
        {
            var first = CorpusGenerators.Generate(file.Generator!);
            var second = CorpusGenerators.Generate(file.Generator!);
            Assert.Equal(first, second);
            Assert.Equal(file.Sha256, CorpusStore.Sha256Hex(first));
            Assert.Equal(file.Bytes, first.LongLength);
        }
    }

    [Fact]
    public void EveryGeneratorIsInTheManifestSoNoneIsLeftUntested()
    {
        var used = CorpusManifest.Load(ManifestPath).Files.Where(f => f.Generator is not null).Select(f => f.Generator!).ToHashSet();
        Assert.Equal(CorpusGenerators.All.Keys.Order(), used.Order());
    }

    [Fact]
    public void TheFixturesTheIntegrationTestsUseHaveTheHashesTheirManifestRecords()
    {
        // The corpus entries for the five shared fixtures and ADR-0010's hashes must agree with the committed copies.
        var fixtures = Path.Combine(Locations.RepositoryRoot()!, "tests", "MikePDF.Integration.Tests", "Fixtures");
        foreach (var file in CorpusManifest.Load(ManifestPath).Files)
        {
            var name = Path.GetFileName(file.Id);
            var committed = Path.Combine(fixtures, name);
            if (file.Origin == CorpusOrigin.Fetch && file.Id.StartsWith("pdfium/", StringComparison.Ordinal) && File.Exists(committed))
            {
                Assert.Equal(file.Sha256, CorpusStore.Sha256Hex(File.ReadAllBytes(committed)));
            }
        }
    }

    // ---- the checks themselves ------------------------------------------------------------------

    [Fact]
    public void AnUnverifiedLicenceIsRefusedInAnyTier()
    {
        var problems = ManifestValidator.Validate(Manifest(Entry(licence: "UNVERIFIED")));
        Assert.Contains(problems, p => p.Contains("not verified", StringComparison.Ordinal));
        Assert.Contains(ManifestValidator.Validate(Manifest(Entry(licence: ""))), p => p.Contains("not verified", StringComparison.Ordinal));
        Assert.Contains(ManifestValidator.Validate(Manifest(Entry(licence: "GPL-3.0-only"))), p => p.Contains("ADR-0003", StringComparison.Ordinal));
    }

    [Fact]
    public void BadIdsHashesTiersAndOriginsAreEachNamed()
    {
        var bad = ManifestValidator.Validate(Manifest(
            Entry("../escape.pdf"),
            Entry("/rooted.pdf"),
            Entry("back\\slash.pdf"),
            Entry("dup.pdf"),
            Entry("DUP.pdf"),
            Entry("short-hash.pdf", sha: "abc"),
            Entry("upper.pdf", sha: new string('A', 64)),
            Entry("gen.pdf", origin: CorpusOrigin.Generated, generator: "no-such-generator"),
            Entry("odd.pdf", origin: "carrier-pigeon"),
            Entry("neg.pdf", bytes: -1)));
        string[] expected = ["relative path", "relative path", "relative path", "appears twice", "64 lower-case hex", "unknown generator", "unknown origin", "cannot be negative"];
        foreach (var fragment in expected)
        {
            Assert.Contains(bad, p => p.Contains(fragment, StringComparison.Ordinal));
        }

        var tier = Entry("t.pdf");
        Assert.Contains(ManifestValidator.Validate(Manifest(tier with { Tiers = ["nightly"] })), p => p.Contains("unknown tier", StringComparison.Ordinal));
        Assert.Contains(ManifestValidator.Validate(Manifest(tier with { Redistributable = false })), p => p.Contains("redistributed", StringComparison.Ordinal));
        Assert.Contains(ManifestValidator.Validate(Manifest(tier with { Source = "http://example.test/a.pdf" })), p => p.Contains("https", StringComparison.Ordinal));
        Assert.Contains(ManifestValidator.Validate(Manifest(tier with { Expect = new CorpusExpect { Opens = true, Status = "Damaged" } })), p => p.Contains("expect.opens", StringComparison.Ordinal));
        Assert.Contains(ManifestValidator.Validate(Manifest(tier with { Render = new CorpusRender { Pages = "x" } })), p => p.Contains("render.pages", StringComparison.Ordinal));
    }

    [Fact]
    public void ASmokeTierThatIsTooSmallOrMissesACoverageTagIsRefused()
    {
        var problems = ManifestValidator.Validate(Manifest(Entry("only.pdf", tags: ["text"])));
        Assert.Contains(problems, p => p.Contains("at least 30", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("'jbig2'", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("'cropbox-offset'", StringComparison.Ordinal));
    }

    [Fact]
    public void AnExcludedSourceMustSayWhy()
    {
        var manifest = Manifest(Entry()) with { Excluded = [new ExcludedSource { Source = "https://example.test", Reason = " " }] };
        Assert.Contains(ManifestValidator.Validate(manifest), p => p.Contains("say why", StringComparison.Ordinal));
    }

    // ---- fetching and verifying -----------------------------------------------------------------

    private sealed class FakeServer(Func<string, byte[]?> serve) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            var body = serve(request.RequestUri.ToString());
            return Task.FromResult(body is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }

    private static CorpusFile Fetched(string id, byte[] content, string? encoding = null, string? sha = null) => Entry(id, sha ?? CorpusStore.Sha256Hex(content), content.Length) with
    {
        Source = "https://example.test/" + id,
        FetchEncoding = encoding,
    };

    [Fact]
    public async Task AFetchKeepsOnlyFilesWhoseHashMatchesAndDecodesBase64()
    {
        var plain = "%PDF-1.4 plain"u8.ToArray();
        var coded = "%PDF-1.4 coded"u8.ToArray();
        var manifest = Manifest(Fetched("p/plain.pdf", plain), Fetched("p/coded.pdf", coded, "base64"));
        using var server = new FakeServer(url => url.EndsWith("plain.pdf", StringComparison.Ordinal) ? plain : Encoding.ASCII.GetBytes(Convert.ToBase64String(coded) + "\n"));
        var directory = Path.Combine(Path.GetTempPath(), "mikepdf-render-tests", "fetch-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var summary = await new CorpusFetcher(new HttpClient(server)).FetchAsync(manifest, "smoke", directory, null, null, CancellationToken.None);
            Assert.Equal((2, 0, 0, 0), (summary.Downloaded, summary.Generated, summary.AlreadyPresent, summary.Failures.Count));
            Assert.Equal(plain, File.ReadAllBytes(CorpusStore.PathFor(directory, manifest.Files[0])));
            Assert.Equal(coded, File.ReadAllBytes(CorpusStore.PathFor(directory, manifest.Files[1])));
            Assert.Empty(CorpusStore.Verify(manifest, "smoke", directory));

            // A second fetch finds everything in place and asks the server for nothing.
            server.Requests.Clear();
            var again = await new CorpusFetcher(new HttpClient(server)).FetchAsync(manifest, "smoke", directory, null, null, CancellationToken.None);
            Assert.Equal((0, 2), (again.Downloaded, again.AlreadyPresent));
            Assert.Empty(server.Requests);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AFileThatDoesNotMatchItsHashFailsTheFetchAndIsNotKept()
    {
        var expected = "%PDF-1.4 the real one"u8.ToArray();
        var manifest = Manifest(Fetched("p/a.pdf", expected));
        using var server = new FakeServer(_ => "%PDF-1.4 something else"u8.ToArray());
        var directory = Path.Combine(Path.GetTempPath(), "mikepdf-render-tests", "fetch-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var summary = await new CorpusFetcher(new HttpClient(server)).FetchAsync(manifest, "smoke", directory, null, null, CancellationToken.None);
            Assert.Equal(0, summary.Downloaded);
            var failure = Assert.Single(summary.Failures);
            Assert.Contains("expected sha256", failure, StringComparison.Ordinal);
            Assert.False(File.Exists(CorpusStore.PathFor(directory, manifest.Files[0])));
            Assert.Throws<InvalidDataException>(() => CorpusStore.Resolve(manifest, "smoke", directory));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task AMissingFileOnTheServerIsAFailureNotACrashAndIsNotRetried()
    {
        var manifest = Manifest(Fetched("p/gone.pdf", "%PDF"u8.ToArray()));
        using var server = new FakeServer(_ => null);
        var directory = Path.Combine(Path.GetTempPath(), "mikepdf-render-tests", "fetch-" + Guid.NewGuid().ToString("N")[..8]);
        var summary = await new CorpusFetcher(new HttpClient(server), TimeSpan.Zero).FetchAsync(manifest, "smoke", directory, null, null, CancellationToken.None);
        Assert.Single(summary.Failures);
        Assert.Single(server.Requests); // a 404 is not going to change
    }

    [Fact]
    public async Task AServerErrorIsRetriedAFewTimesAndThenSucceedsOrFails()
    {
        var content = "%PDF-1.4 flaky"u8.ToArray();
        var manifest = Manifest(Fetched("p/flaky.pdf", content));
        var directory = Path.Combine(Path.GetTempPath(), "mikepdf-render-tests", "fetch-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var calls = 0;
            using var recovers = new FlakyServer(() => ++calls < 3 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK, content);
            var summary = await new CorpusFetcher(new HttpClient(recovers), TimeSpan.Zero).FetchAsync(manifest, "smoke", directory, null, null, CancellationToken.None);
            Assert.Equal((1, 0), (summary.Downloaded, summary.Failures.Count));
            Assert.Equal(3, calls);

            using var never = new FlakyServer(() => HttpStatusCode.BadGateway, content);
            var other = Path.Combine(directory, "second");
            var failed = await new CorpusFetcher(new HttpClient(never), TimeSpan.Zero).FetchAsync(manifest, "smoke", other, null, null, CancellationToken.None);
            Assert.Single(failed.Failures);
            Assert.Equal(5, never.Calls);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private sealed class FlakyServer(Func<HttpStatusCode> status, byte[] body) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var code = status();
            return Task.FromResult(new HttpResponseMessage(code) { Content = code == HttpStatusCode.OK ? new ByteArrayContent(body) : new ByteArrayContent([]) });
        }
    }

    [Fact]
    public void VerifyFindsAMissingFileAndAFileThatWasChanged()
    {
        var content = "%PDF-1.4 content"u8.ToArray();
        var manifest = Manifest(Fetched("p/a.pdf", content), Fetched("p/b.pdf", content));
        var directory = Path.Combine(Path.GetTempPath(), "mikepdf-render-tests", "verify-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CorpusStore.PathFor(directory, manifest.Files[0]))!);
            File.WriteAllBytes(CorpusStore.PathFor(directory, manifest.Files[0]), "%PDF-1.4 tampered"u8.ToArray());
            var problems = CorpusStore.Verify(manifest, "smoke", directory);
            Assert.Equal(2, problems.Count);
            Assert.Contains(problems, p => p.StartsWith("p/a.pdf: expected sha256", StringComparison.Ordinal));
            Assert.Contains(problems, p => p.StartsWith("p/b.pdf: missing", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task AGeneratedFileIsMadeLocallyWithoutTheNetworkAndAChangedGeneratorIsCaught()
    {
        var bytes = CorpusGenerators.Generate("truncated");
        var good = Entry("generated/t.pdf", CorpusStore.Sha256Hex(bytes), bytes.Length, CorpusOrigin.Generated, "truncated");
        var stale = good with { Id = "generated/stale.pdf", Sha256 = new string('0', 64) };
        using var server = new FakeServer(_ => throw new InvalidOperationException("a generated file must not touch the network"));
        var directory = Path.Combine(Path.GetTempPath(), "mikepdf-render-tests", "gen-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var summary = await new CorpusFetcher(new HttpClient(server)).FetchAsync(Manifest(good, stale), "smoke", directory, null, null, CancellationToken.None);
            Assert.Equal(1, summary.Generated);
            Assert.Empty(server.Requests);
            Assert.Contains("generator changed", Assert.Single(summary.Failures), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void ATierFilterSelectsByIdAndByTier()
    {
        var manifest = Manifest(Entry("pdfium/a.pdf"), Entry("pdfium/b.pdf") with { Tiers = ["core"] }, Entry("generated/c.pdf"));
        Assert.Equal(["pdfium/a.pdf", "generated/c.pdf"], CorpusStore.InTier(manifest, "smoke").Select(f => f.Id));
        Assert.Equal(["generated/c.pdf"], CorpusStore.InTier(manifest, "smoke", "GENERATED").Select(f => f.Id));
        Assert.Equal(["pdfium/a.pdf", "pdfium/b.pdf", "generated/c.pdf"], CorpusStore.InTier(manifest, "core").Select(f => f.Id)); // the tiers nest: core runs the smoke files too
    }
}
