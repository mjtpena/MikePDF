using Quire.Broker;
using Quire.Integration.Tests.Support;
using Quire.PdfWriter;
using Quire.Protocol;

namespace Quire.Integration.Tests;

/// <summary>
/// ADR-0008: <c>Quire.WriterHost</c> in its own sandbox. It is a self-contained .NET process started through the same launcher and AppContainer
/// profile as the engine; the only difference ADR-0004 allows is that dynamic code is not prohibited, because the JIT needs it.
/// </summary>
public sealed class WriterHostTests(SandboxFixture sandbox)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private string PdfWriterFixtures => Path.Combine(sandbox.RepositoryRoot, "tests", "Quire.PdfWriter.Tests", "Fixtures", "pdfium");

    public static TheoryData<string> FixtureNames
    {
        get
        {
            var directory = Path.Combine(FindRoot(), "tests", "Quire.PdfWriter.Tests", "Fixtures", "pdfium");
            return [.. Directory.GetFiles(directory, "*.pdf").Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal)];
        }
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Quire.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Quire.slnx not found");
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public async Task TheHostValidatesEveryPdfWriterFixtureLikeAnInProcessValidatorWould(string name)
    {
        var path = Path.Combine(PdfWriterFixtures, name);
        await using var host = await sandbox.StartWriterHostAsync();

        var result = await host.ValidateStructureAsync(path);

        Assert.Equal(Status.Ok, result.Status);
        var reply = result.Value!;
        var oracle = StructureValidator.Validate(File.ReadAllBytes(path));
        Assert.Equal(oracle.IsValid, reply.Ok);
        var expectedIssues = oracle.Issues.OrderByDescending(i => i.Severity).Take(256).ToList();
        Assert.Equal(expectedIssues.Select(i => (uint)i.Code), reply.IssueCodes);
        Assert.Equal(oracle.Issues.Count > 256, reply.Truncated);
        Assert.All(reply.IssueMessages, m => Assert.True(System.Text.Encoding.UTF8.GetByteCount(m) <= 192));
        Assert.Equal(0, host.RestartCount);
    }

    [Fact]
    public async Task AWellFormedFileIsOkAndAMalformedOneGetsIssuesNotACrash()
    {
        using var good = new TempFile("good.pdf", File.ReadAllBytes(SandboxFixture.FixturePath("rectangles.pdf")));
        using var garbage = new TempFile("garbage.pdf", "this is not a pdf, though it is a file"u8.ToArray());
        var truncatedBytes = File.ReadAllBytes(SandboxFixture.FixturePath("hello_world.pdf"));
        using var truncated = new TempFile("truncated.pdf", truncatedBytes[..(truncatedBytes.Length / 2)]);
        using var empty = new TempFile("empty.pdf", []);
        var noise = new byte[100_000];
        new Random(7).NextBytes(noise);
        using var random = new TempFile("noise.pdf", noise);
        await using var host = await sandbox.StartWriterHostAsync();
        var process = host.ProcessId;

        var valid = await host.ValidateStructureAsync(good.Path);
        Assert.Equal(Status.Ok, valid.Status);
        Assert.True(valid.Value!.Ok);

        foreach (var broken in new[] { garbage, truncated, empty, random })
        {
            var result = await host.ValidateStructureAsync(broken.Path);
            Assert.Equal(Status.Ok, result.Status);
            Assert.False(result.Value!.Ok, broken.Path);
            Assert.NotEmpty(result.Value.IssueCodes);
            Assert.Equal(result.Value.IssueCodes.Length, result.Value.IssueMessages.Length);
            Assert.Contains(result.Value.IssueMessages, m => m.StartsWith("error: ", StringComparison.Ordinal));
        }

        Assert.Equal(process, host.ProcessId); // the same host, still alive
        Assert.Equal(0, host.RestartCount);
        Assert.Equal(Status.Ok, (await host.ValidateStructureAsync(good.Path)).Status);
    }

    [Fact]
    public async Task AFileThatCannotBeOpenedIsRefusedByTheBrokerBeforeTheHostSeesIt()
    {
        await using var host = await sandbox.StartWriterHostAsync();
        Assert.Equal(Status.InvalidArgument, (await host.ValidateStructureAsync(Path.Combine(Path.GetTempPath(), "quire-no-such.pdf"))).Status);
    }

    [Fact]
    public async Task ManyIssuesAreCutToTheSchemaLimitsWithTheTruncatedFlag()
    {
        // 600 objects the xref points at in the wrong place: more issues than a frame may carry.
        var pdf = new System.Text.StringBuilder("%PDF-1.7\n");
        pdf.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        var xref = pdf.Length;
        pdf.Append("xref\n0 603\n0000000000 65535 f \n");
        pdf.Append("0000000009 00000 n \n0000000050 00000 n \n");
        for (var i = 0; i < 600; i++)
        {
            pdf.Append("0000000013 00000 n \n");
        }

        pdf.Append("trailer\n<< /Size 603 /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        using var many = new TempFile("many.pdf", System.Text.Encoding.Latin1.GetBytes(pdf.ToString()));
        await using var host = await sandbox.StartWriterHostAsync();

        var result = await host.ValidateStructureAsync(many.Path);

        Assert.Equal(Status.Ok, result.Status);
        Assert.False(result.Value!.Ok);
        Assert.True(result.Value.IssueCodes.Length <= 256);
        Assert.True(result.Value.Truncated, $"expected more than 256 issues, got {result.Value.IssueCodes.Length}");
        Assert.All(result.Value.IssueMessages, m => Assert.True(System.Text.Encoding.UTF8.GetByteCount(m) <= 192));
    }

    [Fact]
    public async Task KillingTheHostRespawnsItAndTheNextJobRuns()
    {
        using var good = new TempFile("good.pdf", File.ReadAllBytes(SandboxFixture.FixturePath("rectangles.pdf")));
        await using var host = await sandbox.StartWriterHostAsync();
        var restored = new TaskCompletionSource<EngineRestoredEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var crashed = new TaskCompletionSource<EngineCrashedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Restored += (_, e) => restored.TrySetResult(e);
        host.HostCrashed += (_, e) => crashed.TrySetResult(e);
        Assert.Equal(Status.Ok, (await host.ValidateStructureAsync(good.Path)).Status);
        var first = host.ProcessId;

        host.Supervisor.CurrentProcess!.Kill(1);

        var crash = await crashed.Task.WaitAsync(Wait);
        var restore = await restored.Task.WaitAsync(Wait);
        Assert.Contains(crash.Reason, new[] { EngineFailureReason.ProcessExited, EngineFailureReason.PipeClosed });
        Assert.NotEqual(first, host.ProcessId);
        Assert.Equal(Status.Ok, (await host.ValidateStructureAsync(good.Path)).Status);
        var line = $"writerhost kill: crash to restored in {restore.Elapsed.TotalMilliseconds:F0} ms";
        TestContext.Current.SendDiagnosticMessage(line);
        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "restore-timings.txt"), line + Environment.NewLine);
    }

    [Fact]
    public async Task AFrozenHostIsKilledByTheWatchdogLikeAnEngine()
    {
        using var good = new TempFile("good.pdf", File.ReadAllBytes(SandboxFixture.FixturePath("rectangles.pdf")));
        await using var host = await sandbox.StartWriterHostAsync(o => o with { PingInterval = TimeSpan.FromMilliseconds(200), WatchdogTimeout = TimeSpan.FromSeconds(1.5) } /* test-only override: the defaults are 1 s and 5 s (DefaultsTests) */);
        var crashed = new TaskCompletionSource<EngineCrashedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var restored = new TaskCompletionSource<EngineRestoredEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.HostCrashed += (_, e) => crashed.TrySetResult(e);
        host.Restored += (_, e) => restored.TrySetResult(e);

        NativeTestMethods.Suspend(host.Supervisor.CurrentProcess!.NativeHandle);

        Assert.Equal(EngineFailureReason.Hung, (await crashed.Task.WaitAsync(Wait)).Reason);
        await restored.Task.WaitAsync(Wait);
        Assert.Equal(Status.Ok, (await host.ValidateStructureAsync(good.Path)).Status);
    }

    [Fact]
    public async Task EveryNegativeAccessProbeIsDeniedUnderTheWriterHostProfile()
    {
        using var runner = new ProbeRunner(sandbox);
        var policy = new SandboxPolicy { ProhibitDynamicCode = false }; // the one thing ADR-0004 lets differ
        var control = await runner.RunControlAsync();
        var sandboxed = await runner.RunSandboxedAsync(policy);

        ProbeAssertions.AssertEverythingIsDenied(control, sandboxed);
        ProbeAssertions.AssertMitigations(sandboxed, dynamicCodeProhibited: false);
    }

    [Fact]
    public async Task TheHostIsInTheSandboxAndSaysWhatItDidAboutTheSignaturePolicy()
    {
        await using var host = await sandbox.StartWriterHostAsync();
        var log = string.Empty;
        for (var attempt = 0; attempt < 100 && !log.Contains("signature policy", StringComparison.Ordinal); attempt++)
        {
            await Task.Delay(50);
            log = host.Supervisor.CurrentProcess!.LogText;
        }

        TestContext.Current.SendDiagnosticMessage("writerhost log: " + log.ReplaceLineEndings(" | "));
        Assert.Contains("writerhost: signature policy applied", log, StringComparison.Ordinal);
    }

    // ---- the host spoken to frame by frame ----------------------------------------------------------

    [Fact]
    public async Task HelloIsAnsweredWithNoPdfiumAndAnotherSchemaVersionEndsTheHost()
    {
        await using (var host = await RawEngine.StartWriterHostAsync(sandbox))
        {
            var reply = (HelloReply)(await host.HelloAsync()).Payload!;
            Assert.Equal(ProtocolInfo.SchemaVersion, reply.SchemaVersion);
            Assert.Equal(string.Empty, reply.PdfiumVersion);
            Assert.Equal(7UL, ((Pong)(await host.CallAsync(0, new Ping { Nonce = 7 })).Payload!).Nonce);
            Assert.Equal(Status.Ok, (await host.CallAsync(0, new Shutdown())).Status);
            Assert.True(await host.WaitForExitAsync(Wait));
            Assert.Equal(0u, host.Process.ExitCode);
        }

        await using var other = await RawEngine.StartWriterHostAsync(sandbox);
        var refusal = await other.CallAsync(0, new Hello { SchemaVersion = ProtocolInfo.SchemaVersion + 1, ProcessKind = ProcessKind.Broker, Pid = 1 });
        Assert.Equal(Status.Unsupported, refusal.Status);
        Assert.True(await other.WaitForExitAsync(Wait));
        Assert.Equal(5u, other.Process.ExitCode);
    }

    [Theory]
    [InlineData("a request before Hello")]
    [InlineData("an engine message")]
    [InlineData("garbage")]
    [InlineData("a length above the frame limit")]
    public async Task AnythingOutOfSequenceOrMalformedEndsTheHost(string kind)
    {
        await using var host = await RawEngine.StartWriterHostAsync(sandbox);
        if (kind != "a request before Hello")
        {
            await host.HelloAsync();
        }

        switch (kind)
        {
            case "a request before Hello":
                await host.SendAsync(0, new Ping { Nonce = 1 });
                break;
            case "an engine message":
                await host.SendAsync(0, new OpenDocument { Handle = 1 });
                break;
            case "garbage":
                await host.SendRawAsync([5, 0, 0, 0, 0xC1, 0xC1, 0xC1, 0xC1, 0xC1]);
                break;
            default:
                await host.SendRawAsync([0x01, 0x00, 0x01, 0x00]);
                break;
        }

        Assert.Null(await host.ReceiveAsync(Wait));
        Assert.True(await host.WaitForExitAsync(Wait));
        Assert.Equal(5u, host.Process.ExitCode);
    }

    [Fact]
    public async Task AHandleThatIsNotAReadableFileIsRefusedAsAnInvalidArgument()
    {
        await using var host = await RawEngine.StartWriterHostAsync(sandbox);
        await host.HelloAsync();
        // A real handle of the wrong kind: an event (a bogus value would trip strict handle checks, as it does in the engine).
        using var wrongKind = new ManualResetEvent(false);
        var remote = host.Process.DuplicateFileInto(new Microsoft.Win32.SafeHandles.SafeFileHandle(wrongKind.SafeWaitHandle.DangerousGetHandle(), ownsHandle: false));
        var response = await host.CallAsync(0, new ValidateStructure { Handle = remote });
        Assert.Equal(Status.InvalidArgument, response.Status);
        Assert.False(host.Process.HasExited);
        Assert.Equal(Status.Ok, (await host.CallAsync(0, new Ping { Nonce = 1 })).Status);
    }
}
