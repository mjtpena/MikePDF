using System.Diagnostics;
using MikePDF.Core.Printing;
using MikePDF.Integration.Tests.Support;
using MikePDF.Printing;
using MikePDF.Protocol;

namespace MikePDF.Integration.Tests.Printing;

/// <summary>
/// The pipeline with the real sandboxed engine being killed, and the memory a long job holds. A printer that keeps pixels (or discards them) stands in for the driver, so these always run.
/// Criteria PRINT-10 (reduced: see the test), PRINT-12.
/// </summary>
public sealed class PrintRecoveryTests(SandboxFixture sandbox)
{
    private static readonly PaperArea A4At150 = new(1240, 1754, 150, 150);

    private static PrintJobRequest Request(int pageCount, PrintOptions options, int maxCrashes = 3) => new()
    {
        PrinterName = "Memory",
        DocumentName = "Recovery test",
        Pages = PageSelection.Resolve(PrintScope.AllPages, pageCount, 1, null, null, PageSubset.All).Pages,
        Options = options,
        MaxEngineCrashes = maxCrashes,
    };

    private static void KillEngine(PrintSession session) => session.Engine.Supervisor.CurrentProcess!.Kill(1);

    [Fact]
    public async Task AnEngineKilledMidJobIsReplacedAndTheJobFinishesWithEveryPageOnceAndTheSamePixels()
    {
        await using var harness = new PrintHarness(sandbox);
        var options = new PrintOptions { Scaling = PrintScaling.Fit, FormFields = false };
        var pdf = PrintFixtures.Text(6);

        var baseline = new BitmapDevice(A4At150);
        await using (var session = await harness.OpenAsync(pdf, options))
        {
            var done = await PrintJob.RunAsync(Request(6, options), session.Source, new FixedDeviceFactory(baseline));
            Assert.True(done.Completed, done.Detail);
        }

        var killed = new BitmapDevice(A4At150);
        await using (var session = await harness.OpenAsync(pdf, options))
        {
            var firstEngine = session.Engine.EngineProcessId;
            var kills = 0;
            killed.AfterDraw = draws =>
            {
                if (draws is 1 or 3) // the band after the next one is still to be asked for, so a kill is always noticed
                {
                    kills++;
                    KillEngine(session);
                }
            };
            var result = await PrintJob.RunAsync(Request(6, options), session.Source, new FixedDeviceFactory(killed));

            Assert.True(result.Completed, $"{result.Outcome}: {result.Detail}");
            Assert.Equal(2, kills);
            Assert.Equal(2, result.EngineCrashes);
            Assert.NotEqual(firstEngine, session.Engine.EngineProcessId);
            Assert.True(session.Engine.RestartCount >= 2);
        }

        Assert.Equal(6, killed.Sheets.Count); // no page lost, none printed twice
        Assert.Equal(baseline.Draws, killed.Draws);
        for (var page = 0; page < 6; page++)
        {
            Assert.True(baseline.Sheets[page].AsSpan().SequenceEqual(killed.Sheets[page]), $"page {page + 1} printed after an engine crash is not the page printed without one");
        }
    }

    [Fact]
    public async Task AfterThreeKillsTheJobFailsWithTheMessageAndTheSpoolJobIsAborted()
    {
        await using var harness = new PrintHarness(sandbox);
        var options = new PrintOptions { Scaling = PrintScaling.Fit, FormFields = false };
        await using var session = await harness.OpenAsync(PrintFixtures.Text(8), options);
        var device = new BitmapDevice(A4At150);
        device.AfterDraw = _ => KillEngine(session); // the engine dies after every band
        var result = await PrintJob.RunAsync(Request(8, options), session.Source, new FixedDeviceFactory(device));

        Assert.Equal(PrintOutcome.Failed, result.Outcome);
        Assert.Equal(Status.EngineCrashed, result.EngineStatus);
        Assert.Equal("Print_Failed", result.ResourceKey);
        Assert.Contains("3", result.Detail);
        Assert.True(device.Aborted && !device.Ended);
        Assert.True(device.Sheets.Count < 8);
    }

    // ---- PRINT-10, reduced ----
    // The criterion is a 1,000 page job to Microsoft Print to PDF against a 10 page job. That is about an hour and 3 GB of output on this queue (ADR-0011), so it is a manual run and is
    // UNVERIFIED here. What is checked is the part that belongs to MikePDF: with the printer discarding the pixels, the broker's and the engine's private bytes during a 60 page job
    // stay within 5% of a 10 page job's (plus a fixed allowance for the runtime's own noise). The spooler's growth, which the ADR measured separately, is not in this number.

    [Fact]
    public async Task BrokerAndEnginePrivateBytesDoNotGrowWithThePageCount()
    {
        await using var harness = new PrintHarness(sandbox);
        var options = new PrintOptions { Scaling = PrintScaling.Fit, FormFields = false };
        var paper = new PaperArea(2480, 3508, 300, 300); // A4 at 300 dpi: 35 Mpixels of device pixels in total, 8.7 Mpixels a page

        var (brokerShort, engineShort) = await PeakAsync(harness, 10, options, paper);
        var (brokerLong, engineLong) = await PeakAsync(harness, 60, options, paper);

        PrintEvidence.Note($"memory: broker peak private bytes {brokerShort / 1048576.0:F1} MiB (10 pages) and {brokerLong / 1048576.0:F1} MiB (60 pages); engine {engineShort / 1048576.0:F1} and {engineLong / 1048576.0:F1} MiB");
        const long allowance = 16 << 20;
        Assert.True(brokerLong <= (brokerShort * 1.05) + allowance, $"broker peak {brokerLong} bytes for 60 pages against {brokerShort} for 10");
        Assert.True(engineLong <= (engineShort * 1.05) + allowance, $"engine peak {engineLong} bytes for 60 pages against {engineShort} for 10");
    }

    private static async Task<(long Broker, long Engine)> PeakAsync(PrintHarness harness, int pages, PrintOptions options, PaperArea paper)
    {
        await using var session = await harness.OpenAsync(PrintFixtures.Text(pages), options);
        var device = new NullDevice(paper);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long broker = 0;
        long engine = 0;
        using var stop = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            using var self = Process.GetCurrentProcess();
            while (!stop.IsCancellationRequested)
            {
                self.Refresh();
                broker = Math.Max(broker, self.PrivateMemorySize64);
                try
                {
                    using var child = Process.GetProcessById((int)session.Engine.EngineProcessId);
                    engine = Math.Max(engine, child.PrivateMemorySize64);
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // The engine is between processes, or gone.
                }

                await Task.Delay(20, CancellationToken.None);
            }
        });

        var result = await PrintJob.RunAsync(Request(pages, options), session.Source, new FixedDeviceFactory(device));
        await stop.CancelAsync();
        await sampler;
        Assert.True(result.Completed, result.Detail);
        Assert.Equal(pages, device.Pages);
        return (broker, engine);
    }
}
