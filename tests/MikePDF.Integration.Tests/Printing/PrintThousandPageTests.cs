using System.Diagnostics;
using MikePDF.Core.Printing;
using MikePDF.Integration.Tests.Support;
using MikePDF.Printing;

namespace MikePDF.Integration.Tests.Printing;

/// <summary>
/// PRINT-10 in full: a 1,000 page job to Microsoft Print to PDF against a 10 page job, the broker's and the engine's peak private bytes, sampled while the job runs. It takes the better part of
/// an hour and a few GB of output on this queue (ADR-0011), so it runs only when asked: set <c>MIKEPDF_PRINT_1000=1</c>. The pages (and so the time) are set with <c>MIKEPDF_PRINT_1000_PAGES</c>
/// (default 1000) and the resolution is the queue's own. The numbers go to <c>build/out/print-evidence.txt</c> and are quoted in docs/features/print.md.
/// </summary>
public sealed class PrintThousandPageTests(SandboxFixture sandbox)
{
    private static readonly string[] SpoolerSide = ["spoolsv", "PrintFilterPipelineSvc", "PrintIsolationHost"];

    private sealed record Peaks(long Broker, long Engine, long Spooler, TimeSpan Elapsed, int Sheets, long OutputBytes);

    [Fact]
    public async Task ThePrivateBytesOfTheBrokerAndTheEngineAreFlatFromTenPagesToAThousand()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MIKEPDF_PRINT_1000") == "1", "PRINT-10 in full is opt-in (about an hour): set MIKEPDF_PRINT_1000=1");
        VirtualPrinter.Require();
        var pages = int.TryParse(Environment.GetEnvironmentVariable("MIKEPDF_PRINT_1000_PAGES"), out var n) ? n : 1000;
        await using var harness = new PrintHarness(sandbox);
        var options = new PrintOptions { PaperSizeId = VirtualPrinter.PaperA4, Scaling = PrintScaling.Fit, Orientation = PaperOrientation.Portrait, FormFields = false };

        var small = await MeasureAsync(harness, 10, options);
        PrintEvidence.Note($"PRINT-10 10 pages: broker peak {small.Broker / 1048576.0:F1} MiB, engine peak {small.Engine / 1048576.0:F1} MiB, spooler side peak {small.Spooler / 1048576.0:F1} MiB, {small.Elapsed.TotalSeconds:F0} s, output {small.OutputBytes / 1048576.0:F1} MiB");
        var large = await MeasureAsync(harness, pages, options);
        PrintEvidence.Note($"PRINT-10 {pages} pages: broker peak {large.Broker / 1048576.0:F1} MiB, engine peak {large.Engine / 1048576.0:F1} MiB, spooler side peak {large.Spooler / 1048576.0:F1} MiB, {large.Elapsed.TotalSeconds:F0} s ({large.Elapsed.TotalSeconds / pages:F2} s per page), output {large.OutputBytes / 1048576.0:F1} MiB");

        Assert.Equal(pages, large.Sheets);
        Assert.True(large.Broker <= small.Broker * 1.05, $"broker peak {large.Broker} bytes for {pages} pages against {small.Broker} for 10");
        Assert.True(large.Engine <= small.Engine * 1.05, $"engine peak {large.Engine} bytes for {pages} pages against {small.Engine} for 10");
    }

    private static async Task<Peaks> MeasureAsync(PrintHarness harness, int pages, PrintOptions options)
    {
        await using var session = await harness.OpenAsync(PrintFixtures.Text(pages), options);
        var output = harness.Temp("thousand-" + pages + ".pdf");
        var request = new PrintJobRequest
        {
            PrinterName = VirtualPrinter.Name,
            DocumentName = "MikePDF PRINT-10",
            Pages = PageSelection.Resolve(PrintScope.AllPages, session.PageCount, 1, null, null, PageSubset.All).Pages,
            Options = options,
            OutputPath = output,
            BandDeadline = TimeSpan.FromSeconds(60),
        };

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long broker = 0;
        long engine = 0;
        long spooler = 0;
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

                // The spooler's side of the job (spoolsv and the print filter pipeline host), which ADR-0011 watched grow: recorded, not asserted.
                long side = 0;
                foreach (var name in SpoolerSide)
                {
                    foreach (var process in Process.GetProcessesByName(name))
                    {
                        using (process)
                        {
                            try
                            {
                                side += process.PrivateMemorySize64;
                            }
                            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
                            {
                                // Gone, or not ours to read.
                            }
                        }
                    }
                }

                spooler = Math.Max(spooler, side);
                await Task.Delay(250, CancellationToken.None);
            }
        });

        var clock = Stopwatch.StartNew();
        var result = await PrintJob.RunAsync(request, session.Source, GdiPrintDeviceFactory.Instance);
        clock.Stop();
        await stop.CancelAsync();
        await sampler;
        Assert.True(result.Completed, result.Detail);
        return new Peaks(broker, engine, spooler, clock.Elapsed, result.SheetsPrinted, new FileInfo(output).Length);
    }
}
