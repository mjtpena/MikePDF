using System.Diagnostics;
using System.Text;
using MikePDF.Integration.Tests.Support;
using MikePDF.Protocol;
using MikePDF.ShellHost;

namespace MikePDF.Integration.Tests;

/// <summary>ShellHost with the real sandboxed engine (<c>--kind shell</c>) behind it: correct pages, the fixtures that must fail cleanly, no engine left behind, and a real crash (SHELL-03, -04, -08, -12, -13).</summary>
public sealed class ShellHostEngineTests(SandboxFixture sandbox)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private SandboxedShellEngineFactory RealEngines() => new(sandbox.EnginePath, sandbox.Profile);

    private sealed record Thumbnail(uint Width, uint Height, byte[] Bgra);

    private static async Task<(Status Status, ShellOpenReply? Open)> OpenAsync(ShellTestClient client, byte[] pdf)
    {
        using var section = TestSection.Of(pdf);
        var opened = await client.OpenAsync(section, 5000);
        return (opened.Status, opened.Payload as ShellOpenReply);
    }

    private static async Task<Thumbnail> ThumbnailAsync(ShellHostHarness host, byte[] pdf, uint edge)
    {
        await using var client = host.Connect();
        await client.HelloAsync();
        var (status, open) = await OpenAsync(client, pdf);
        Assert.Equal(Status.Ok, status);
        using var pixels = new TestSection(edge * edge * 4);
        var rendered = await client.RenderAsync(open!.DocumentId, pixels, edge, edge, deadlineMs: 5000);
        Assert.Equal(Status.Ok, rendered.Status);
        var page = (ShellRenderPageReply)rendered.Payload!;
        Assert.Equal(page.Width * 4, page.Stride);
        return new Thumbnail(page.Width, page.Height, pixels.Read((int)(page.Stride * page.Height)));
    }

    private static (byte B, byte G, byte R) At(Thumbnail t, int x, int y)
    {
        var offset = (y * (int)t.Width + x) * 4;
        return (t.Bgra[offset], t.Bgra[offset + 1], t.Bgra[offset + 2]);
    }

    private static byte[] Fixture(string name) => File.ReadAllBytes(SandboxFixture.FixturePath(name));

    /// <summary>A one-page PDF with extra page-dictionary entries (a /Rotate, a /CropBox) and a content stream of coloured squares.</summary>
    private static byte[] PageWith(string mediaBox, string pageExtra, string content)
    {
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();

        void Add(string body)
        {
            offsets.Add(pdf.Length);
            pdf.Append(offsets.Count).Append(" 0 obj\n").Append(body).Append("\nendobj\n");
        }

        Add("<< /Type /Catalog /Pages 2 0 R >>");
        Add($"<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [{mediaBox}] >>");
        Add($"<< /Type /Page /Parent 2 0 R /Contents 4 0 R {pageExtra} >>");
        Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        var xref = pdf.Length;
        pdf.Append("xref\n0 ").Append(offsets.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            pdf.Append(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }

        pdf.Append("trailer\n<< /Size ").Append(offsets.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(pdf.ToString());
    }

    // ---- SHELL-03 and SHELL-04: the right pixels ----------------------------------------------------------------------------------------

    [Fact]
    public async Task APageComesBackWithItsFourColouredCornersInTheRightPlaces()
    {
        await using var host = ShellHostHarness.Start(RealEngines());
        var t = await ThumbnailAsync(host, Fixture("rectangles.pdf"), 256);
        Assert.Equal((171u, 256u), (t.Width, t.Height)); // 200 x 300 pt into 256 x 256
        Assert.Equal((0, 0, 0), At(t, 3, 3));                       // top left: black
        Assert.Equal((255, 0, 0), At(t, (int)t.Width - 4, 3));      // top right: blue (BGR)
        Assert.Equal((0, 255, 0), At(t, (int)t.Width - 4, (int)t.Height - 4)); // bottom right: green
        Assert.Equal((0, 0, 255), At(t, 3, (int)t.Height - 4));     // bottom left: red
        Assert.Equal((255, 255, 255), At(t, (int)t.Width / 2, (int)t.Height / 2 - 60)); // paper
    }

    [Theory]
    [InlineData("rectangles.pdf", 32u)]
    [InlineData("rectangles.pdf", 96u)]
    [InlineData("rectangles.pdf", 256u)]
    [InlineData("rectangles.pdf", 1024u)]
    [InlineData("hello_world.pdf", 32u)]
    [InlineData("hello_world.pdf", 96u)]
    [InlineData("hello_world.pdf", 256u)]
    [InlineData("hello_world.pdf", 1024u)]
    public async Task TheLongerSideEqualsCxAndTheAspectRatioHoldsWithinAPixel(string fixture, uint cx)
    {
        await using var host = ShellHostHarness.Start(RealEngines());
        await using var client = host.Connect();
        await client.HelloAsync();
        var (_, open) = await OpenAsync(client, Fixture(fixture));
        var t = await ThumbnailAsync(host, Fixture(fixture), cx);
        // The page is never scaled above twice its natural size (spec 3.3): a small fixture at 1024 is as large as that allows.
        var natural = Math.Max(open!.FirstWidth, open.FirstHeight);
        Assert.Equal((uint)Math.Min(cx, Math.Round(2 * natural)), Math.Max(t.Width, t.Height));
        var expected = (double)open.FirstWidth / open.FirstHeight;
        Assert.InRange((double)t.Width / t.Height, expected - 1.0 / t.Height - 1e-9, expected + 1.0 / t.Height + 1e-9);
    }

    [Fact]
    public async Task ARotatedPageAndACropBoxWithAnOriginAreDrawnAsTheViewerDrawsThem()
    {
        await using var host = ShellHostHarness.Start(RealEngines());
        const string Squares = "1 0 0 rg 0 0 50 50 re f 0 0 1 rg 250 250 50 50 re f";

        // 300 x 200 landscape page turned a quarter: shown 200 wide and 300 tall.
        var rotated = await ThumbnailAsync(host, PageWith("0 0 300 200", "/Rotate 90", Squares), 300);
        Assert.Equal((200u, 300u), (rotated.Width, rotated.Height));

        // A 300 x 300 MediaBox with a CropBox that starts at (50, 50) and is 100 x 200: the thumbnail is the crop, not the media.
        var cropped = await ThumbnailAsync(host, PageWith("0 0 300 300", "/CropBox [50 50 150 250]", "0 0 1 rg 50 50 20 20 re f"), 200);
        Assert.Equal((100u, 200u), (cropped.Width, cropped.Height));
        Assert.Equal((255, 0, 0), At(cropped, 3, (int)cropped.Height - 4)); // the square at the crop's origin is at its bottom left: blue (BGR)

        // A CropBox that reaches outside the MediaBox is limited to it.
        var outside = await ThumbnailAsync(host, PageWith("0 0 100 100", "/CropBox [-50 -50 200 200]", "0 0 0 rg 0 0 10 10 re f"), 100);
        Assert.Equal((100u, 100u), (outside.Width, outside.Height));
    }

    [Fact]
    public async Task APreviewRendersEveryPageOfADocumentAndRefusesOnePastTheEnd()
    {
        await using var host = ShellHostHarness.Start(RealEngines());
        await using var client = host.Connect(ShellFeature.Preview);
        await client.HelloAsync();
        var (status, open) = await OpenAsync(client, Fixture("hello_world_2_pages.pdf"));
        Assert.Equal(Status.Ok, status);
        Assert.Equal(2u, open!.PageCount);
        using var pixels = new TestSection(600 * 1024 * 4);
        for (uint page = 0; page < 2; page++)
        {
            var rendered = await client.RenderAsync(open.DocumentId, pixels, 600, 1024, page, 5000);
            Assert.Equal(Status.Ok, rendered.Status);
            Assert.Equal(600u, ((ShellRenderPageReply)rendered.Payload!).Width);
        }

        Assert.Equal(Status.InvalidArgument, (await client.RenderAsync(open.DocumentId, pixels, 600, 1024, 2)).Status);
    }

    // ---- SHELL-13: files that must fail cleanly --------------------------------------------------------------------------------------

    [Fact]
    public async Task PasswordProtectedNonPdfDamagedAndTruncatedFilesFailCleanlyWithNoEngineLeftBehind()
    {
        var good = Fixture("hello_world.pdf");
        var cases = new (string Name, byte[] Bytes, Status[] Expected)[]
        {
            ("encrypted", Fixture("encrypted.pdf"), [Status.PasswordRequired]),
            ("not a pdf", Encoding.ASCII.GetBytes("This is a text file, not a PDF at all. It has no header and no structure."), [Status.Damaged]),
            ("truncated", good[..(good.Length / 2)], [Status.Damaged]),
            ("bytes flipped", [.. good.Select((b, i) => i is > 20 and < 120 ? (byte)(b ^ 0x5A) : b)], [Status.Damaged, Status.Ok]),
            ("one byte", [0x25], [Status.Damaged]),
        };

        await using (var host = ShellHostHarness.Start(RealEngines()))
        {
            foreach (var (name, bytes, expected) in cases)
            {
                await using var client = host.Connect();
                await client.HelloAsync();
                var (status, _) = await OpenAsync(client, bytes);
                Assert.True(expected.Contains(status), $"{name}: {status}");
                // No dialog is possible here, and the connection is still alive for the next file.
                Assert.Equal(Status.Ok, (await client.CallAsync(new Ping { Nonce = 1 })).Status);
            }

            Assert.True(host.Server.Pool.Live <= 1, "only the spare may be left: " + host.Server.Pool.Live);
            Assert.True(await UntilAsync(() => NativeTestMethods.RunningEnginesIn(sandbox.DeployDirectory) <= 1), "engines left: " + NativeTestMethods.RunningEnginesIn(sandbox.DeployDirectory));
            Assert.Empty(Directory.GetFiles(host.StagingDirectory));
        }

        Assert.True(await UntilAsync(() => NativeTestMethods.RunningEnginesIn(sandbox.DeployDirectory) == 0), "engines left after ShellHost stopped");
    }

    private static async Task<bool> UntilAsync(Func<bool> condition, int milliseconds = 5000)
    {
        for (var waited = 0; waited < milliseconds; waited += 20)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return condition();
    }

    // ---- SHELL-08: a real crash ------------------------------------------------------------------------------------------------------------

    /// <summary>Starts real engines and kills the process of each one the moment it is asked to render: the engine "crashed on the file".</summary>
    private sealed class CrashingEngines(IShellEngineFactory inner) : IShellEngineFactory
    {
        public int Started;

        public async Task<IShellEngine> StartAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Started);
            return new Crashing(await inner.StartAsync(cancellationToken).ConfigureAwait(false));
        }

        private sealed class Crashing(IShellEngine engine) : IShellEngine
        {
            public uint ProcessId => engine.ProcessId;

            public bool IsAlive => engine.IsAlive;

            public Task<ShellResult<ShellDocumentInfo>> OpenAsync(string path, CancellationToken cancellationToken) => engine.OpenAsync(path, cancellationToken);

            public async Task<ShellResult<ShellRender>> RenderAsync(uint page, uint maxWidth, uint maxHeight, bool annotations, float maxScale, CancellationToken cancellationToken)
            {
                using (var process = Process.GetProcessById((int)engine.ProcessId))
                {
                    process.Kill();
                    await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                }

                return await engine.RenderAsync(page, maxWidth, maxHeight, annotations, maxScale, cancellationToken).ConfigureAwait(false);
            }

            public ValueTask KillAsync() => engine.KillAsync();

            public ValueTask DisposeAsync() => engine.DisposeAsync();
        }
    }

    [Fact]
    public async Task ACrashedEngineFailsTheRequestAndTheSameBytesAreNotTriedAgain()
    {
        var crashing = new CrashingEngines(RealEngines());
        await using var host = ShellHostHarness.Start(crashing);
        var pdf = Fixture("rectangles.pdf");

        async Task<Status> RenderOnce()
        {
            await using var client = host.Connect();
            await client.HelloAsync();
            var (status, open) = await OpenAsync(client, pdf);
            if (status != Status.Ok)
            {
                return status;
            }

            using var pixels = new TestSection(256 * 256 * 4);
            return (await client.RenderAsync(open!.DocumentId, pixels, 256, 256, deadlineMs: 5000)).Status;
        }

        Assert.Equal(Status.EngineCrashed, await RenderOnce());
        Assert.Equal(1, host.Server.Cache.Count);

        // The second time the answer comes from the cache: no document is opened in any engine (an open would have crashed it on the render, so count the starts).
        var started = crashing.Started;
        Assert.Equal(Status.EngineCrashed, await RenderOnce());
        Assert.True(crashing.Started <= started + 1, "a spare is replaced in the background, but no engine is started for the cached file");
        Assert.Equal(1, host.Server.Cache.Count);

        // And a different file is served normally by a healthy engine (the doubles only crash on render, so use ShellHost's own pool without them).
        await using var healthy = ShellHostHarness.Start(RealEngines());
        Assert.Equal(171u, (await ThumbnailAsync(healthy, pdf, 256)).Width);
    }

    // ---- SHELL-12: a flood -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ThirtyConcurrentRequestsNeverRunMoreThanThreeEnginesAndAllFinish()
    {
        await using var host = ShellHostHarness.Start(RealEngines());
        var pdf = Fixture("rectangles.pdf");
        var started = Stopwatch.StartNew();
        var results = await Task.WhenAll(Enumerable.Range(0, 30).Select(async i =>
        {
            // Different bytes each time, so nothing is shared and each request needs its own engine.
            byte[] bytes = [.. pdf, .. Encoding.ASCII.GetBytes("\n% " + i)];
            await using var client = host.Connect();
            await client.HelloAsync();
            using var section = TestSection.Of(bytes);
            var opened = await client.OpenAsync(section, 5000);
            if (opened.Status != Status.Ok)
            {
                return opened.Status;
            }

            using var pixels = new TestSection(96 * 96 * 4);
            return (await client.RenderAsync(((ShellOpenReply)opened.Payload!).DocumentId, pixels, 96, 96, deadlineMs: 5000)).Status;
        })).WaitAsync(TimeSpan.FromMinutes(2));

        Assert.All(results, status => Assert.Equal(Status.Ok, status));
        Assert.True(host.Server.Pool.Peak <= 3, $"{host.Server.Pool.Peak} engines were alive at once");
        Assert.True(await UntilAsync(() => NativeTestMethods.RunningEnginesIn(sandbox.DeployDirectory) <= 1));
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(60), started.Elapsed.ToString());
    }
}
