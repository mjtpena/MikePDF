using MikePDF.Core.Geometry;
using MikePDF.Core.Printing;
using MikePDF.Integration.Tests.Support;
using MikePDF.Printing;

namespace MikePDF.Integration.Tests.Printing;

/// <summary>
/// PRINT-12's "one engine death is one crash", under load and with the timings varied: the call that dies, the delay of the call in flight beside it, and the time the supervisor takes to
/// replace the engine are all drawn at random for each of 300 runs, with a busy thread on every core (more threads than that starve the timers so badly that 300 runs take a quarter of an hour). A run that counted a death twice, or not at all, or lost or doubled a band, fails.
/// </summary>
public sealed class PrintCrashStressTests
{
    private static readonly int Iterations = int.TryParse(Environment.GetEnvironmentVariable("MIKEPDF_PRINT_STRESS_ITERATIONS"), out var n) && n > 0 ? n : 40;
    private static readonly PaperArea A4At300 = new(2480, 3508, 300, 300);
    private static readonly SizeD Letter = new(612, 792);

    [Fact]
    public async Task OneEngineDeathIsOneCrashWhateverTheTimingAndTheJobStillPrintsEveryBandOnce()
    {
        var pages = PageSelection.Resolve(PrintScope.AllPages, 2, 1, null, null, PageSubset.All).Pages;
        PrintJobRequest Request() => new() { PrinterName = "Fake", DocumentName = "Stress", Pages = pages, Options = new PrintOptions() };

        var reference = new FakeDevice(A4At300);
        var clean = await PrintJob.RunAsync(Request(), new FakePrintSource(Letter, Letter) { SlotBytes = 8 << 20 }, new FakeDeviceFactory(reference));
        Assert.True(clean.Completed);
        Assert.True(reference.Draws.Count >= 8, "the job has enough bands for a death to catch two of them");

        var random = new Random(20261103);
        using var load = CpuLoad.OnEveryCore();
        for (var i = 0; i < Iterations; i++)
        {
            var dies = random.Next(2, 6); // the call that is being served when the engine dies; the next call is in flight beside it
            var sibling = TimeSpan.FromMilliseconds(random.Next(1, 30));
            var restart = random.Next(3) == 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(random.Next(1, 15));
            var source = new FakePrintSource(Letter, Letter)
            {
                SlotBytes = 8 << 20,
                DieDuringCall = (_, call) => call == dies,
                DelayFor = (_, call) => call == dies + 1 ? sibling : TimeSpan.FromMilliseconds(1),
                RestartDelay = restart,
            };
            var device = new FakeDevice(A4At300);

            var result = await PrintJob.RunAsync(Request(), source, new FakeDeviceFactory(device));

            var run = $"run {i}: dies at call {dies}, sibling {sibling.TotalMilliseconds} ms, restart {restart.TotalMilliseconds} ms";
            Assert.True(result.Completed, $"{run}: {result.Detail}");
            Assert.True(source.Deaths == 1, $"{run}: {source.Deaths} deaths");
            Assert.True(result.EngineCrashes == 1, $"{run}: counted {result.EngineCrashes} crashes for one death");
            Assert.True(reference.Draws.SequenceEqual(device.Draws), $"{run}: a band was lost, doubled or moved");
            Assert.True(source.Bands.All(b => b.Disposed == 1), $"{run}: a slot was not given back exactly once");
        }
    }
}
