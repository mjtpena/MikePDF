using MikePDF.Broker;
using MikePDF.Core.Settings;
using MikePDF.Integration.Tests.Support;
using MikePDF.Protocol;

namespace MikePDF.Integration.Tests;

/// <summary>
/// W2-engine-2 items 3 and 4: what the broker takes from the administrator's policy (the engine memory limit reaches the job object of every engine it starts) and what it can tell the
/// About dialog (PDFium's version, without a document).
/// </summary>
public sealed class EnginePolicyTests(SandboxFixture sandbox)
{
    private const long Mib = 1L << 20;

    private EngineBudget NewBudget(int? limitMb, Func<EngineSupervisorOptions, EngineSupervisorOptions>? shape = null) => new(new EngineBudgetOptions
    {
        Document = sandbox.Options(o => (shape ?? (x => x))(o with { SlotCount = 4, SlotSize = 1 << 20 })),
        Pool = sandbox.Options(o => (shape ?? (x => x))(o with { SlotCount = 4, SlotSize = 1 << 20 })),
        EngineMemoryLimitMb = limitMb,
    });

    private static PolicyProvider Policy(int? megabytes)
    {
        var source = new FixedPolicySource();
        if (megabytes is { } mb)
        {
            source.Values[PolicyScope.Machine].Add(new RawPolicyValue("EngineMemoryLimitMB", RawPolicyKind.Dword, mb));
        }

        return new PolicyProvider(source);
    }

    [Theory]
    [InlineData(512)]
    [InlineData(1024)]
    [InlineData(4096)]
    public async Task APolicyMemoryLimitReachesTheJobOfTheDocumentEngineAndThePoolEngine(int megabytes)
    {
        // The value an administrator sets, read the way the app reads it, handed to the budget the way EngineHost hands it.
        var limit = Policy(megabytes).Current.EngineMemoryLimitMb;
        Assert.Equal(megabytes, limit);

        await using var budget = NewBudget(limit);
        var pool = await budget.GetPoolAsync();
        Assert.Equal(megabytes * Mib, pool.CurrentProcess!.Job.QueryProcessMemoryLimitBytes());

        var engine = await budget.CreateDocumentEngineAsync();
        Assert.Equal(megabytes * Mib, engine.Supervisor.CurrentProcess!.Job.QueryProcessMemoryLimitBytes());
    }

    [Fact]
    public async Task WithoutAPolicyTheLimitIsTheDefaultTwoGibibytes()
    {
        var limit = Policy(null).Current.EngineMemoryLimitMb;
        Assert.Equal(2048, limit);
        await using var budget = NewBudget(limit);
        Assert.Equal(2048 * Mib, (await budget.GetPoolAsync()).CurrentProcess!.Job.QueryProcessMemoryLimitBytes());

        await using var unset = NewBudget(null);
        Assert.Equal(SandboxPolicy.Default.ProcessMemoryLimitBytes, (await unset.GetPoolAsync()).CurrentProcess!.Job.QueryProcessMemoryLimitBytes());
    }

    [Fact]
    public async Task ALimitThatSurvivesARestartStillAppliesToTheNewEngine()
    {
        await using var budget = NewBudget(768);
        var engine = await budget.CreateDocumentEngineAsync();
        var first = engine.Supervisor.CurrentProcess!;
        Assert.Equal(768 * Mib, first.Job.QueryProcessMemoryLimitBytes());

        Assert.True(await engine.SuspendAsync());
        Assert.Equal(Status.Ok, await engine.ResumeAsync());
        var second = engine.Supervisor.CurrentProcess!;
        Assert.NotSame(first, second);
        Assert.Equal(768 * Mib, second.Job.QueryProcessMemoryLimitBytes());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(EngineBudgetOptions.MinEngineMemoryLimitMb - 1)]
    [InlineData(EngineBudgetOptions.MaxEngineMemoryLimitMb + 1)]
    public void ALimitOutsideWhatAnEngineCanRunInIsRefused(int megabytes) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => NewBudget(megabytes));

    [Fact]
    public async Task APolicyLimitIsEnforcedNotOnlyRecorded()
    {
        // 256 MiB of whitespace in a page's content stream: PDFium decodes a content stream in one piece, and the policy allows 128 MB (the same page under the default limit draws).
        using var hungry = new TempFile("hungry.pdf", TestPdf.HungryPage(256 << 20));
        await using var budget = NewBudget(128);
        var engine = await budget.CreateDocumentEngineAsync();
        var opened = await engine.OpenDocumentAsync(hungry.Path);
        Assert.Equal(Status.Ok, opened.Status);
        await using var document = opened.Value!;

        var render = await document.RenderTileAsync(new RenderTileArgs(0, 1f, 0, 0, 0, 64, 64), 1);

        Assert.Equal(Status.EngineCrashed, render.Status);
    }

    // ---- item 4: the PDFium version without a document -------------------------------------------------------------------------------

    [Fact]
    public async Task ThePdfiumVersionIsAvailableWithoutAnOpenDocumentAndStartsThePoolEngine()
    {
        await using var budget = NewBudget(null);
        Assert.Equal(0u, budget.PoolProcessId);

        var version = await budget.GetPdfiumVersionAsync();

        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.Matches(@"^\d+\.\d+\.\d+\.\d+$", version!);
        Assert.NotEqual(0u, budget.PoolProcessId); // the pool engine was started for it
        Assert.Equal((await budget.GetPoolAsync()).CurrentProcess!.ProcessId, budget.PoolProcessId);
    }

    [Fact]
    public async Task ThePdfiumVersionIsTheOneTheEngineSaysInItsHelloAndIsAskedOnlyOnce()
    {
        await using var budget = NewBudget(null);
        var version = await budget.GetPdfiumVersionAsync();
        var pool = await budget.GetPoolAsync();
        Assert.Equal(pool.PdfiumVersion, version);

        // Known now: the second answer comes from memory, not from a process.
        var pid = budget.PoolProcessId;
        Assert.Equal(version, await budget.GetPdfiumVersionAsync());
        Assert.Equal(pid, budget.PoolProcessId);
    }

    [Fact]
    public async Task ThePdfiumVersionIsNullWhenNoEngineCanBeStarted()
    {
        await using var budget = new EngineBudget(new EngineBudgetOptions
        {
            Document = sandbox.Options(o => o with { EnginePath = Path.Combine(sandbox.DeployDirectory, "no-such-engine.exe") }),
            Pool = sandbox.Options(o => o with { EnginePath = Path.Combine(sandbox.DeployDirectory, "no-such-engine.exe") }),
        });

        Assert.Null(await budget.GetPdfiumVersionAsync());
    }

    [Fact]
    public async Task ThePdfiumVersionAfterTheBudgetIsDisposedIsNullNotAnException()
    {
        var budget = NewBudget(null);
        await budget.DisposeAsync();
        Assert.Null(await budget.GetPdfiumVersionAsync());
    }

    [Fact]
    public async Task TheVersionIsTheSameAsTheOneADocumentEngineReports()
    {
        await using var budget = NewBudget(null);
        var engine = await budget.CreateDocumentEngineAsync();
        Assert.Equal(engine.Supervisor.PdfiumVersion, await budget.GetPdfiumVersionAsync());
    }
}
