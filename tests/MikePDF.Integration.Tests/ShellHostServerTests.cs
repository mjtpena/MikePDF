using System.Diagnostics;
using System.Runtime.InteropServices;
using MikePDF.Core.Settings;
using MikePDF.Integration.Tests.Support;
using MikePDF.Protocol;
using MikePDF.ShellHost;

namespace MikePDF.Integration.Tests;

/// <summary>
/// ShellHost's pipe, handshake, requests, deadlines and caches against engine doubles (spec 3.2, 3.3, 11; ADR-0018): SHELL-07, SHELL-08, SHELL-09's server half, SHELL-10, SHELL-16
/// and SHELL-17. The same requests through the real sandboxed engine are in <see cref="ShellHostEngineTests"/>.
/// </summary>
public sealed class ShellHostServerTests
{
    private static readonly byte[] SomePdf = [.. "%PDF-1.7\n1 0 obj\n<< >>\nendobj\n"u8, .. new byte[200]];
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static async Task<bool> UntilAsync(Func<bool> condition, int milliseconds = 3000)
    {
        for (var waited = 0; waited < milliseconds; waited += 10)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(10);
        }

        return condition();
    }

    // ---- the thumbnail round trip ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task HelloOpenRenderAndClose()
    {
        var factory = new FakeShellEngineFactory();
        await using var host = ShellHostHarness.Start(factory);
        await using var client = host.Connect();
        var hello = await client.HelloAsync();
        Assert.Equal(ProtocolInfo.SchemaVersion, ((HelloReply)hello.Payload!).SchemaVersion);

        using var document = TestSection.Of(SomePdf);
        var opened = await client.OpenAsync(document);
        Assert.Equal(Status.Ok, opened.Status);
        var reply = (ShellOpenReply)opened.Payload!;
        Assert.Equal(3u, reply.PageCount);
        Assert.Equal(612f, reply.FirstWidth);

        using var pixels = new TestSection(256 * 256 * 4);
        var rendered = await client.RenderAsync(reply.DocumentId, pixels, 256, 256);
        Assert.Equal(Status.Ok, rendered.Status);
        var page = (ShellRenderPageReply)rendered.Payload!;
        Assert.Equal((198u, 256u, 198u * 4), (page.Width, page.Height, page.Stride));
        var first = pixels.Read(4);
        Assert.Equal([0x20, 0x40, 0x80, 0x00], first); // the engine's pixels land in the handler's section

        var closed = await client.CallAsync(new ShellClose(), reply.DocumentId);
        Assert.Equal(Status.Ok, closed.Status);
        // The staged bytes are gone as soon as the session is.
        Assert.True(await UntilAsync(() => Directory.GetFiles(host.StagingDirectory).Length == 0));
    }

    [Fact]
    public async Task ThePreviewPipeServesAScrollableSession()
    {
        var factory = new FakeShellEngineFactory();
        await using var host = ShellHostHarness.Start(factory);
        await using var client = host.Connect(ShellFeature.Preview);
        await client.HelloAsync();
        using var document = TestSection.Of(SomePdf);
        var session = ((ShellOpenReply)(await client.OpenAsync(document, 5000)).Payload!).DocumentId;
        using var pixels = new TestSection(1024 * 1024 * 4);
        for (uint page = 0; page < 3; page++)
        {
            var rendered = await client.RenderAsync(session, pixels, 600, 1024, page, 5000);
            Assert.Equal(Status.Ok, rendered.Status);
        }

        Assert.Equal(1, factory.Engines.Count(e => e.Renders == 3)); // one engine for the whole session
        // The preview pipe fits the pane's width: 1000 wide and at most 1024 tall for a 612 x 792 page.
        var preview = (ShellRenderPageReply)(await client.RenderAsync(session, pixels, 1000, 1024)).Payload!;
        Assert.Equal(791u, preview.Width);
    }

    // ---- SHELL-16: the user's switches -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ASwitchedOffFeatureAnswersWithin100MsAndDoesNoWork()
    {
        var factory = new FakeShellEngineFactory();
        await using var host = ShellHostHarness.Start(factory);
        host.Switches = new ShellSwitches(ThumbnailsEnabled: false, PreviewEnabled: true, MaxEngines: 3);
        await using var client = host.Connect();
        await client.HelloAsync();
        using var document = TestSection.Of(SomePdf);

        var started = Stopwatch.StartNew();
        var refused = await client.OpenAsync(document);
        started.Stop();
        Assert.Equal(Status.PermissionDenied, refused.Status);
        Assert.True(started.ElapsedMilliseconds < 100, $"{started.ElapsedMilliseconds} ms");
        Assert.Empty(factory.StagedPaths);
        Assert.Empty(Directory.GetFiles(host.StagingDirectory));

        // The other feature has its own pipe and its own switch.
        await using var preview = host.Connect(ShellFeature.Preview);
        await preview.HelloAsync();
        Assert.Equal(Status.Ok, (await preview.OpenAsync(document)).Status);
    }

    [Fact]
    public async Task TheSwitchesAreReadOnEveryRequestSoTheyApplyWithoutARestart()
    {
        var factory = new FakeShellEngineFactory();
        await using var host = ShellHostHarness.Start(factory);
        using var document = TestSection.Of(SomePdf);

        host.Switches = new ShellSwitches(true, false, 3);
        await using (var preview = host.Connect(ShellFeature.Preview))
        {
            await preview.HelloAsync();
            Assert.Equal(Status.PermissionDenied, (await preview.OpenAsync(document)).Status);
        }

        host.Switches = ShellSwitches.Defaults;
        await using (var preview = host.Connect(ShellFeature.Preview))
        {
            await preview.HelloAsync();
            Assert.Equal(Status.Ok, (await preview.OpenAsync(document)).Status);
        }
    }

    // ---- who may talk to it -------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task OnlyAShellHandlerHelloIsAccepted()
    {
        await using var host = ShellHostHarness.Start(new FakeShellEngineFactory());
        foreach (var kind in new[] { ProcessKind.Broker, ProcessKind.Engine, ProcessKind.WriterHost })
        {
            await using var client = host.Connect();
            await client.SendAsync(new Hello { SchemaVersion = ProtocolInfo.SchemaVersion, ProcessKind = kind, Pid = 1 });
            Assert.True(await client.WaitForCloseAsync(Wait), kind + " got an answer");
        }

        // A request before any Hello is not a handler either.
        await using var rude = host.Connect();
        await rude.SendAsync(new Ping { Nonce = 1 });
        Assert.True(await rude.WaitForCloseAsync(Wait));
    }

    [Fact]
    public async Task AnotherSchemaVersionIsRefusedAndTheConnectionEnds()
    {
        await using var host = ShellHostHarness.Start(new FakeShellEngineFactory());
        await using var client = host.Connect();
        var id = await client.SendAsync(new Hello { SchemaVersion = ProtocolInfo.SchemaVersion + 1, ProcessKind = ProcessKind.ShellHandler, Pid = 1 });
        var reply = await client.ReceiveAsync();
        Assert.Equal(id, reply!.RequestId);
        Assert.Equal(Status.Unsupported, reply.Status);
        Assert.True(await client.WaitForCloseAsync(Wait));
    }

    [Fact]
    public async Task MessagesThatAreNotAHandlersAreNotServed()
    {
        await using var host = ShellHostHarness.Start(new FakeShellEngineFactory());
        foreach (IRequestMessage message in new IRequestMessage[] { new Shutdown(), new OpenDocument { Handle = 4 }, new Hello { SchemaVersion = ProtocolInfo.SchemaVersion, ProcessKind = ProcessKind.ShellHandler, Pid = 1 } })
        {
            await using var client = host.Connect();
            await client.HelloAsync();
            await client.SendAsync(message);
            Assert.True(await client.WaitForCloseAsync(Wait), message.MessageType + " was served");
        }
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("length above the frame limit")]
    [InlineData("zero length")]
    [InlineData("a response where a request belongs")]
    public async Task AMalformedFrameClosesTheConnectionAndNothingElse(string kind)
    {
        await using var host = ShellHostHarness.Start(new FakeShellEngineFactory());
        await using var bad = host.Connect();
        await bad.HelloAsync();
        await bad.SendRawAsync(kind switch
        {
            "garbage" => [5, 0, 0, 0, 0xC1, 0xC1, 0xC1, 0xC1, 0xC1],
            "length above the frame limit" => [0x01, 0x00, 0x01, 0x00],
            "zero length" => [0, 0, 0, 0],
            _ => FakeEngine.Response(1, Status.Ok, MessageType.Ping, new Pong { Nonce = 1 }),
        });
        Assert.True(await bad.WaitForCloseAsync(Wait));

        // Another handler is not affected.
        await using var good = host.Connect();
        await good.HelloAsync();
        Assert.Equal(Status.Ok, (await good.CallAsync(new Ping { Nonce = 9 })).Status);
    }

    [Fact]
    public async Task ASilentConnectionIsDroppedBeforeItSaysHello()
    {
        await using var host = ShellHostHarness.Start(new FakeShellEngineFactory());
        await using var silent = host.Connect();
        Assert.True(await silent.WaitForCloseAsync(TimeSpan.FromSeconds(8)), "the Hello timeout (2 s in this harness) never ended it");
    }

    [Fact]
    public async Task AClientOutsideThePackageFamilyIsClosedBeforeAnythingIsRead()
    {
        // This test process has no package identity, so a ShellHost that requires a family refuses everyone: fail closed (SHELL-17).
        var factory = new FakeShellEngineFactory();
        var prefix = "mikepdf-test-shell-" + Guid.NewGuid().ToString("N")[..12];
        await using var server = new ShellHostServer(new ShellHostOptions
        {
            UserSid = ShellPipe.CurrentUserSid(),
            StagingDirectory = Path.Combine(Path.GetTempPath(), prefix),
            Engines = factory,
            PipeName = f => prefix + (f == ShellFeature.Preview ? ".Preview" : string.Empty),
            RequireClientPackageFamily = true,
        });
        var run = server.RunAsync();
        await using var client = new ShellTestClient(prefix);
        await client.SendAsync(new Hello { SchemaVersion = ProtocolInfo.SchemaVersion, ProcessKind = ProcessKind.ShellHandler, Pid = 1 });
        Assert.True(await client.WaitForCloseAsync(Wait));
        await server.StopAsync();
        await run;
    }

    [Fact]
    public async Task APipeThatAnotherProcessAlreadyOwnsMakesShellHostRefuseToServe()
    {
        var prefix = "mikepdf-test-shell-" + Guid.NewGuid().ToString("N")[..12];
        // The squatter: it created the name first, with the same security a real ShellHost would use.
        using var squatter = ShellPipe.Create(prefix, ShellPipe.Sddl(ShellPipe.CurrentUserSid()), first: true);
        await using var server = new ShellHostServer(new ShellHostOptions
        {
            UserSid = ShellPipe.CurrentUserSid(),
            StagingDirectory = Path.Combine(Path.GetTempPath(), prefix),
            Engines = new FakeShellEngineFactory(),
            PipeName = f => prefix + (f == ShellFeature.Preview ? ".Preview" : string.Empty),
            RequireClientPackageFamily = false,
        });
        await Assert.ThrowsAnyAsync<Exception>(async () => await server.RunAsync());
    }

    [Fact]
    public async Task ThePipeCarriesTheDaclAndTheLowLabelOfTheDecision()
    {
        await using var host = ShellHostHarness.Start(new FakeShellEngineFactory());
        await using var client = host.Connect();
        var sddl = PipeSecurityText(client.Pipe.SafePipeHandle.DangerousGetHandle());
        var sid = ShellPipe.CurrentUserSid();
        Assert.StartsWith("D:P", sddl, StringComparison.Ordinal); // protected: nothing inherited
        // The kernel writes a well-known SID as its alias: the built-in Administrator account that a hosted runner uses reads back as LA, not as its S-1-5-21-... text.
        Assert.True(sddl.Contains(";;;" + sid + ")", StringComparison.Ordinal) || sddl.Contains(";;;LA)", StringComparison.Ordinal), "the current user is not in the DACL: " + sddl);
        Assert.Contains(";;;SY)", sddl, StringComparison.Ordinal);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(sddl[..sddl.IndexOf("S:", StringComparison.Ordinal)], @"\(A;")); // exactly two entries: the user and SYSTEM
        Assert.Contains("(ML;;NW;;;LW)", sddl, StringComparison.Ordinal); // a Low-integrity host may connect
        Assert.DoesNotContain("WD)", sddl, StringComparison.Ordinal); // no Everyone
        Assert.DoesNotContain("AC)", sddl, StringComparison.Ordinal); // no AppContainer: the engine can never connect
    }

    // ---- bad requests -----------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RequestsTheStateDoesNotAllowGetInvalidArgumentAndLeaveTheConnectionUsable()
    {
        await using var host = ShellHostHarness.Start(new FakeShellEngineFactory());
        await using var client = host.Connect();
        await client.HelloAsync();
        using var pixels = new TestSection(256 * 256 * 4);

        // Render and close before any open.
        Assert.Equal(Status.InvalidArgument, (await client.RenderAsync(1, pixels, 256, 256)).Status);
        Assert.Equal(Status.InvalidArgument, (await client.CallAsync(new ShellClose(), 1)).Status);

        using var document = TestSection.Of(SomePdf);
        var session = ((ShellOpenReply)(await client.OpenAsync(document)).Payload!).DocumentId;
        // A second open on the same connection.
        Assert.Equal(Status.InvalidArgument, (await client.OpenAsync(document)).Status);
        // Another session's id, a page past the end, and a pixel section too small for the box.
        Assert.Equal(Status.InvalidArgument, (await client.RenderAsync(session + 1, pixels, 256, 256)).Status);
        Assert.Equal(Status.InvalidArgument, (await client.RenderAsync(session, pixels, 256, 256, page: 3)).Status);
        using var tooSmall = new TestSection(1024);
        Assert.Equal(Status.InvalidArgument, (await client.RenderAsync(session, tooSmall, 256, 256)).Status);
        // And it still works.
        Assert.Equal(Status.Ok, (await client.RenderAsync(session, pixels, 256, 256)).Status);
    }

    [Fact]
    public async Task SectionsThatAreNotWhatTheMessageSaidAreRefusedWithoutHarm()
    {
        await using var host = ShellHostHarness.Start(new FakeShellEngineFactory());
        await using var client = host.Connect();
        await client.HelloAsync();

        // A handle value the client does not have, one that is not a section (an event), and a section smaller than the promised size.
        Assert.Equal(Status.InvalidArgument, (await client.CallAsync(new ShellOpen { Section = 0x7FFF0, Bytes = 100 })).Status);
        using var notASection = new ManualResetEvent(false);
        Assert.Equal(Status.InvalidArgument, (await client.CallAsync(new ShellOpen { Section = (ulong)notASection.SafeWaitHandle.DangerousGetHandle(), Bytes = 100 })).Status);
        using var small = new TestSection(100);
        Assert.Equal(Status.InvalidArgument, (await client.CallAsync(new ShellOpen { Section = small.Handle, Bytes = 100_000 })).Status);

        // The pixel section: valid on open, then a render that names a section too small for what it promised.
        using var document = TestSection.Of(SomePdf);
        var session = ((ShellOpenReply)(await client.OpenAsync(document)).Payload!).DocumentId;
        using var shortPixels = new TestSection(4096);
        var lie = await client.CallAsync(new ShellRenderPage { Page = 0, MaxWidth = 16, MaxHeight = 16, Flags = 1, Pixels = shortPixels.Handle, PixelBytes = 4_194_304 }, session);
        Assert.Equal(Status.InvalidArgument, lie.Status);
        Assert.Equal(Status.InvalidArgument, (await client.CallAsync(new ShellRenderPage { Page = 0, MaxWidth = 16, MaxHeight = 16, Flags = 1, Pixels = 0x7FFF0, PixelBytes = 4096 }, session)).Status);
    }

    // ---- SHELL-07 and SHELL-08: hangs, crashes and the negative cache ------------------------------------------------------------------

    [Fact]
    public async Task AnEngineThatNeverFinishesEndsAtTheDeadlineIsKilledAtOnceAndTheNextRequestWorks()
    {
        var factory = new FakeShellEngineFactory
        {
            OnRender = async (engine, _, _, _, token) =>
            {
                await Task.Delay(Timeout.Infinite, token); // a render that never ends; only a kill or the deadline stops it
                return default;
            },
        };
        await using var host = ShellHostHarness.Start(factory);
        await using var client = host.Connect();
        await client.HelloAsync();
        using var document = TestSection.Of(SomePdf);
        var session = ((ShellOpenReply)(await client.OpenAsync(document)).Payload!).DocumentId;
        var engine = factory.Engines.Single(e => e.Opens == 1);

        using var pixels = new TestSection(256 * 256 * 4);
        var started = Stopwatch.StartNew();
        var hung = await client.RenderAsync(session, pixels, 256, 256, deadlineMs: 400);
        started.Stop();
        Assert.Equal(Status.Timeout, hung.Status);
        Assert.InRange(started.ElapsedMilliseconds, 350, 1500);
        Assert.True(await UntilAsync(() => engine.Killed, 1000), "the engine process outlived its request by more than a second");
        Assert.True(await client.WaitForCloseAsync(Wait), "after a timeout the session is over");

        // The next request, on a new connection, is served by a new engine.
        factory.OnRender = new FakeShellEngineFactory().OnRender;
        using var other = TestSection.Of([.. SomePdf, 1]);
        await using var next = host.Connect();
        await next.HelloAsync();
        var again = ((ShellOpenReply)(await next.OpenAsync(other)).Payload!).DocumentId;
        Assert.Equal(Status.Ok, (await next.RenderAsync(again, pixels, 256, 256)).Status);
    }

    [Fact]
    public async Task AFileThatCrashedTheEngineIsNotTriedAgainUntilTheCacheExpires()
    {
        var clock = new ManualTimeProvider();
        var factory = new FakeShellEngineFactory { OnOpen = (_, _, _) => Task.FromResult(new ShellResult<ShellDocumentInfo>(Status.EngineCrashed, null)) };
        await using var host = ShellHostHarness.Start(factory, time: clock);
        using var bad = TestSection.Of(SomePdf);

        async Task<Status> TryOnce()
        {
            await using var client = host.Connect();
            await client.HelloAsync();
            return (await client.OpenAsync(bad)).Status;
        }

        Assert.Equal(Status.EngineCrashed, await TryOnce());
        var opens = factory.Engines.Sum(e => e.Opens);
        Assert.Equal(1, opens);

        // Within ten minutes: the same bytes fail at once, with no engine and no staging.
        clock.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal(Status.EngineCrashed, await TryOnce());
        Assert.Equal(opens, factory.Engines.Sum(e => e.Opens));
        Assert.Single(factory.StagedPaths);

        // After it: tried again.
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(Status.EngineCrashed, await TryOnce());
        Assert.Equal(opens + 1, factory.Engines.Sum(e => e.Opens));

        // A different file is not affected by any of this.
        factory.OnOpen = (_, _, _) => Task.FromResult(new ShellResult<ShellDocumentInfo>(Status.Ok, new ShellDocumentInfo(1, 100, 100)));
        using var other = TestSection.Of([.. SomePdf, 9, 9]);
        await using var client2 = host.Connect();
        await client2.HelloAsync();
        Assert.Equal(Status.Ok, (await client2.OpenAsync(other)).Status);
    }

    [Fact]
    public async Task AHungOpenIsTheFilesFaultButAFullQueueIsNot()
    {
        var factory = new FakeShellEngineFactory
        {
            OnOpen = async (_, _, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return default;
            },
        };
        await using var host = ShellHostHarness.Start(factory);
        using var document = TestSection.Of(SomePdf);
        await using (var client = host.Connect())
        {
            await client.HelloAsync();
            Assert.Equal(Status.Timeout, (await client.OpenAsync(document, 300)).Status);
        }

        Assert.Equal(1, host.Server.Cache.Count);
        var opens = factory.Engines.Sum(e => e.Opens);
        await using (var again = host.Connect())
        {
            await again.HelloAsync();
            Assert.Equal(Status.Timeout, (await again.OpenAsync(document, 300)).Status);
        }

        Assert.Equal(opens, factory.Engines.Sum(e => e.Opens));
    }

    [Fact]
    public async Task ADeadlineThatPassedInTheQueueIsNotTheFilesFault()
    {
        var factory = new FakeShellEngineFactory();
        await using var host = ShellHostHarness.Start(factory);
        host.Switches = new ShellSwitches(true, true, 1);
        // Occupy the one slot with a document that does not finish.
        factory.OnOpen = async (_, _, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return default;
        };
        using var blocker = TestSection.Of([.. SomePdf, 1]);
        await using var first = host.Connect();
        await first.HelloAsync();
        var firstOpen = first.OpenAsync(blocker, 5000);
        await UntilAsync(() => factory.Engines.Any(e => e.Opens == 1));

        using var queued = TestSection.Of([.. SomePdf, 2]);
        await using var second = host.Connect();
        await second.HelloAsync();
        var queuedReply = await second.OpenAsync(queued, 300);
        Assert.Equal(Status.Timeout, queuedReply.Status);
        Assert.Equal(0, host.Server.Cache.Count);
        _ = firstOpen;
    }

    // ---- sessions end with their connection ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task AClientThatGoesAwayMidRequestHasItsEngineKilledAtOnce()
    {
        var factory = new FakeShellEngineFactory
        {
            OnRender = async (engine, _, _, _, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return default;
            },
        };
        await using var host = ShellHostHarness.Start(factory);
        var client = host.Connect();
        await client.HelloAsync();
        using var document = TestSection.Of(SomePdf);
        var session = ((ShellOpenReply)(await client.OpenAsync(document)).Payload!).DocumentId;
        var engine = factory.Engines.Single(e => e.Opens == 1);
        using var pixels = new TestSection(256 * 256 * 4);
        await client.SendAsync(new ShellRenderPage { Page = 0, MaxWidth = 256, MaxHeight = 256, Flags = 1, Pixels = pixels.Handle, PixelBytes = (uint)pixels.Bytes }, session, 5000);
        await Task.Delay(100);
        var stopwatch = Stopwatch.StartNew();
        await client.DisposeAsync(); // the handler gave up and left

        Assert.True(await UntilAsync(() => engine.Killed, 2000), "the engine kept running for a client that was gone");
        Assert.True(stopwatch.ElapsedMilliseconds < 1500);
        Assert.True(await UntilAsync(() => Directory.GetFiles(host.StagingDirectory).Length == 0), "the staged copy stayed");
        Assert.True(await UntilAsync(() => host.Server.ActiveConnections == 0));
    }

    [Fact]
    public async Task ADroppedConnectionWithAnOpenDocumentFreesItsSlot()
    {
        var factory = new FakeShellEngineFactory();
        await using var host = ShellHostHarness.Start(factory);
        host.Switches = new ShellSwitches(true, true, 1);
        using var document = TestSection.Of(SomePdf);
        for (var i = 0; i < 5; i++)
        {
            await using var client = host.Connect();
            await client.HelloAsync();
            Assert.Equal(Status.Ok, (await client.OpenAsync(document)).Status);
            // ...and the connection is dropped without a ShellClose.
        }

        Assert.True(await UntilAsync(() => host.Server.Pool.Live <= 1));
        Assert.True(factory.Peak <= 1);
    }

    [Fact]
    public async Task StagedFilesOfAnEarlierRunAreDeletedWhenShellHostStarts()
    {
        var prefix = "mikepdf-test-shell-" + Guid.NewGuid().ToString("N")[..12];
        var staging = Path.Combine(Path.GetTempPath(), prefix);
        Directory.CreateDirectory(staging);
        var stale = Path.Combine(staging, "left-behind.pdf");
        File.WriteAllBytes(stale, SomePdf);
        await using var server = new ShellHostServer(new ShellHostOptions
        {
            UserSid = ShellPipe.CurrentUserSid(),
            StagingDirectory = staging,
            Engines = new FakeShellEngineFactory(),
            PipeName = f => prefix + (f == ShellFeature.Preview ? ".Preview" : string.Empty),
            RequireClientPackageFamily = false,
        });
        var run = server.RunAsync();
        Assert.False(File.Exists(stale));
        await server.StopAsync();
        await run;
        Directory.Delete(staging, recursive: true);
    }

    // ---- SHELL-10: idle exit ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ShellHostLeavesWhenIdleButNotWhileAConnectionIsOpen()
    {
        var factory = new FakeShellEngineFactory();
        await using var host = ShellHostHarness.Start(factory, idle: TimeSpan.FromMilliseconds(500));
        var client = host.Connect();
        await client.HelloAsync();

        await Task.Delay(1500);
        Assert.False(host.Run.IsCompleted, "ShellHost left while a handler was still connected (a preview can stay open for as long as a person reads)");

        await client.DisposeAsync();
        await host.Run.WaitAsync(TimeSpan.FromSeconds(10)); // idle exit
        Assert.True(await UntilAsync(() => factory.Alive == 0), "engines outlived ShellHost: " + factory.Alive);
    }

    [Fact]
    public async Task EveryRequestRestartsTheIdleClock()
    {
        await using var host = ShellHostHarness.Start(new FakeShellEngineFactory(), idle: TimeSpan.FromMilliseconds(800));
        for (var i = 0; i < 4; i++)
        {
            await using var client = host.Connect();
            await client.HelloAsync();
            await Task.Delay(300);
        }

        Assert.False(host.Run.IsCompleted);
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint GetSecurityInfo(nint handle, int objectType, uint securityInfo, nint owner, nint group, out nint dacl, nint sacl, out nint descriptor);

    [DllImport("advapi32.dll", EntryPoint = "ConvertSecurityDescriptorToStringSecurityDescriptorW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertSecurityDescriptorToStringSecurityDescriptor(nint descriptor, uint revision, uint information, out nint text, out uint length);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);

    private static string PipeSecurityText(nint handle)
    {
        const uint dacl = 0x4, label = 0x10;
        Assert.Equal(0u, GetSecurityInfo(handle, 6 /* SE_KERNEL_OBJECT */, dacl | label, 0, 0, out _, 0, out var descriptor));
        try
        {
            Assert.True(ConvertSecurityDescriptorToStringSecurityDescriptor(descriptor, 1, dacl | label, out var text, out _));
            try
            {
                return Marshal.PtrToStringUni(text)!;
            }
            finally
            {
                LocalFree(text);
            }
        }
        finally
        {
            LocalFree(descriptor);
        }
    }
}
