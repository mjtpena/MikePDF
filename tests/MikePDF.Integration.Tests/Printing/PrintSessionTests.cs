using MikePDF.Broker;
using MikePDF.Core.Printing;
using MikePDF.Integration.Tests.Support;
using MikePDF.Printing;
using MikePDF.Protocol;

namespace MikePDF.Integration.Tests.Printing;

/// <summary>A print job's engine and the budget (ADR-0004 amendment A3, tabs-windows-session 3.8): it takes a slot like any document engine, never evicts the document being read, and is the size a band needs.</summary>
public sealed class PrintSessionTests(SandboxFixture sandbox)
{
    private EngineBudget NewBudget(int cap, TimeSpan slotWait) => new(new EngineBudgetOptions
    {
        Document = sandbox.Options(o => o with { SlotCount = 4, SlotSize = 1 << 20 }),
        Pool = sandbox.Options(o => o with { SlotCount = 4, SlotSize = 1 << 20 }),
        MaxDocumentEngines = cap,
        SlotWaitTimeout = slotWait,
    });

    private static string Fixture(PrintHarness harness) => harness.Temp("session.pdf", PrintFixtures.Text(2));

    [Fact]
    public async Task APrintEngineHasARingOfBandSizedSlotsAndImageModeGetsWholePageSlots()
    {
        await using var budget = NewBudget(3, TimeSpan.FromSeconds(30));
        await using var harness = new PrintHarness(sandbox);
        var path = Fixture(harness);

        var bands = await PrintSession.OpenAsync(budget, path, null, new PrintOptions(), printingAllowedByPolicy: true);
        Assert.True(bands.IsOk, bands.Detail);
        var bandSession = bands.Value!;
        await using (bandSession)
        {
            Assert.Equal(PrintSession.BandSlotBytes, bandSession.Engine.Supervisor.Ring.SlotSize);
            Assert.Equal(PrintSession.BandSlotCount, bandSession.Engine.Supervisor.Ring.SlotCount);
            Assert.Equal(2, bandSession.PageCount);
            Assert.Equal(PrintSession.BandSlotBytes, bandSession.Source.SlotBytes);
        }

        var image = await PrintSession.OpenAsync(budget, path, null, new PrintOptions { AsImage = true }, printingAllowedByPolicy: true);
        Assert.True(image.IsOk, image.Detail);
        var imageSession = image.Value!;
        await using (imageSession)
        {
            Assert.Equal(SlotRing.MaxSlotSize, imageSession.Engine.Supervisor.Ring.SlotSize);
            Assert.Equal(PrintSession.ImageSlotCount, imageSession.Engine.Supervisor.Ring.SlotCount);
        }

        Assert.Equal(0, budget.LiveDocumentEngines); // disposing the session gave the slot back
    }

    [Fact]
    public async Task APolicyThatTurnsPrintingOffStartsNoEngineAtAll()
    {
        await using var budget = NewBudget(3, TimeSpan.FromSeconds(30));
        await using var harness = new PrintHarness(sandbox);
        var denied = await PrintSession.OpenAsync(budget, Fixture(harness), null, new PrintOptions(), printingAllowedByPolicy: false);
        Assert.Equal(Status.PermissionDenied, denied.Status);
        Assert.Equal("Print_Disabled_Policy", denied.Detail);
        Assert.Equal(0, budget.LiveDocumentEngines);
        Assert.Empty(budget.DocumentEngines);
    }

    [Fact]
    public async Task APrintTakesTheSlotOfTheLeastRecentlyUsedIdleEngineAndLeavesTheOneBeingReadAlone()
    {
        await using var budget = NewBudget(2, TimeSpan.FromSeconds(30));
        await using var harness = new PrintHarness(sandbox);
        var path = Fixture(harness);

        var background = await budget.CreateDocumentEngineAsync();
        var backgroundDocument = (await background.OpenDocumentAsync(path)).Value!;
        var reading = await budget.CreateDocumentEngineAsync();
        var readingDocument = (await reading.OpenDocumentAsync(path)).Value!;
        using var pin = reading.Pin(); // the visible tab
        Assert.Equal(2, budget.LiveDocumentEngines);
        var readingProcess = reading.EngineProcessId;

        var printing = await PrintSession.OpenAsync(budget, path, null, new PrintOptions(), printingAllowedByPolicy: true);

        Assert.True(printing.IsOk, printing.Detail);
        await using (printing.Value!)
        {
            Assert.Equal(DocumentEngineState.Suspended, background.State); // the idle one gave way
            Assert.Equal(DocumentEngineState.Running, reading.State); // the pinned one did not
            Assert.Equal(readingProcess, reading.EngineProcessId);
            Assert.True((await readingDocument.GetInfoAsync()).IsOk, "the document being read still answers while the job runs");
        }

        Assert.True((await backgroundDocument.GetInfoAsync()).IsOk, "the one that gave way comes back when it is asked");
        await background.DisposeAsync();
        await reading.DisposeAsync();
    }

    [Fact]
    public async Task APrintWaitsForASlotWhenEveryEngineIsPinnedThenFailsWithTimeoutAndNothingIsEvicted()
    {
        await using var budget = NewBudget(1, TimeSpan.FromSeconds(1));
        await using var harness = new PrintHarness(sandbox);
        var path = Fixture(harness);
        var reading = await budget.CreateDocumentEngineAsync();
        var readingDocument = (await reading.OpenDocumentAsync(path)).Value!;
        using var pin = reading.Pin();

        var printing = await PrintSession.OpenAsync(budget, path, null, new PrintOptions(), printingAllowedByPolicy: true);

        Assert.Equal(Status.Timeout, printing.Status);
        Assert.Equal(DocumentEngineState.Running, reading.State);
        Assert.True((await readingDocument.GetInfoAsync()).IsOk);
        Assert.Equal(1, budget.LiveDocumentEngines);
        await reading.DisposeAsync();
    }

    [Fact]
    public async Task AFileThatCannotBeOpenedFailsTheSessionAndGivesTheSlotBack()
    {
        await using var budget = NewBudget(2, TimeSpan.FromSeconds(30));
        await using var harness = new PrintHarness(sandbox);
        var missing = await PrintSession.OpenAsync(budget, Path.Combine(Path.GetTempPath(), "mikepdf-no-such-file-" + Guid.NewGuid().ToString("N") + ".pdf"), null, new PrintOptions(), printingAllowedByPolicy: true);
        Assert.False(missing.IsOk);
        Assert.Equal(0, budget.LiveDocumentEngines);

        var garbage = harness.Temp("garbage.pdf", [1, 2, 3, 4, 5]);
        var damaged = await PrintSession.OpenAsync(budget, garbage, null, new PrintOptions(), printingAllowedByPolicy: true);
        Assert.False(damaged.IsOk);
        Assert.Equal(0, budget.LiveDocumentEngines);
    }
}
