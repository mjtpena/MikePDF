using System.Diagnostics;
using System.Globalization;
using MikePDF.PdfWriter.Assembly;
using MikePDF.PdfWriter.Tests.Support;

namespace MikePDF.PdfWriter.Tests;

/// <summary>
/// Measurements, not gates (the work package asks for time and memory of merging two 1,000-page files): what is recorded goes to
/// <c>build/out/perf/assembler-merge-2x1000.txt</c> and to the test output. The only assertions are that the result is right.
/// </summary>
[Trait("Category", "Measurement")]
public sealed class AssemblerMeasurementTests
{
    private static string? OutputDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MikePDF.slnx")))
                return Path.Combine(dir.FullName, "build", "out", "perf");
        }

        return null;
    }

    [Fact]
    [Trait("Spec", "PO-26")]
    public void MergingTwoThousandPageFilesIsMeasured()
    {
        var first = MiniPdf.Large("P", 1000, 11);
        var second = MiniPdf.Large("Q", 1000, 12);
        var sources = new[] { new AssemblerSource("first", first), new AssemblerSource("second", second) };
        var plan = AssemblyPlan.Merge((0, 1000), (1, 1000));
        var options = new AssemblerOptions { WrapSourceOutlines = true };

        // Warm up the JIT and the page cache on a small job, so the numbers are of the work and not of the first call.
        Assert.True(PageAssembler.Assemble([new AssemblerSource("w", MiniPdf.Large("W", 20, 1))], AssemblyPlan.Range(0, 0, 19), options, TestContext.Current.CancellationToken).IsSuccess);

        // The in-memory merge (open both, assemble into one buffer), then the same merge written straight into a file.
        var memory = Measure(() =>
        {
            var set = PageAssembler.Open(sources, options);
            return set.Assemble(plan, TestContext.Current.CancellationToken);
        });
        var result = memory.Result;

        var path = Path.Combine(Path.GetTempPath(), "mikepdf-merge-" + Guid.NewGuid().ToString("N") + ".pdf");
        long streamedBytes;
        Measured<bool> streamed;
        try
        {
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
            streamed = Measure(() =>
            {
                var written = PageAssembler.Open(sources, options).AssembleTo(plan, file, TestContext.Current.CancellationToken);
                Assert.True(written.IsSuccess, written.IsSuccess ? "" : written.Refusal.ToString());
                return true;
            });
            file.Flush();
            streamedBytes = file.Length;
        }
        finally
        {
            File.Delete(path);
        }

        Assert.True(result.IsSuccess, result.IsSuccess ? "" : result.Refusal.ToString());
        var output = result.Output;
        Assert.Equal(2000, output.PageCount);
        Assert.Equal(output.BytesWritten, streamedBytes);
        var document = AssemblyFixtures.CheckStructure(output);
        Assert.Equal(2000, DocInfo.PageCount(document));
        if (Pdfium.UnavailableReason is null)
            Assert.Equal(2000, Pdfium.Open(output.Bytes.ToArray())!.PageCount);

        string Mb(long bytes) => (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture);
        var lines = new[]
        {
            $"merge of two 1,000-page files (assembler, outline wrappers on) on {Environment.ProcessorCount} logical processors, .NET {Environment.Version}, {DateTime.UtcNow:yyyy-MM-dd}",
            $"inputs: {Mb(first.Length)} MB + {Mb(second.Length)} MB (held in memory by the test, as the writer host would map them); output: {Mb(output.BytesWritten)} MB, {output.ObjectCount} objects",
            $"into memory: {memory.Elapsed.TotalMilliseconds:0} ms for open and assemble; allocated {Mb(memory.Allocated)} MB; working set grew by up to {Mb(memory.PeakGrowth)} MB",
            $"straight into a file: {streamed.Elapsed.TotalMilliseconds:0} ms for open and assemble; allocated {Mb(streamed.Allocated)} MB; working set grew by up to {Mb(streamed.PeakGrowth)} MB",
            "a measurement, not a gate; one run each on a busy development machine; the inputs are synthetic (compressed text, a 96 KB image every fifth page), not a scan",
        };
        var directory = OutputDirectory();
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllLines(Path.Combine(directory, "assembler-merge-2x1000.txt"), lines);
        }

        foreach (var line in lines)
            TestContext.Current.SendDiagnosticMessage(line);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
    }

    [Fact]
    [Trait("Spec", "PO-26")]
    public void MergingTwoRealThousandPageRangesFromTheCorpusIsMeasured()
    {
        // Two enrolled bills of the corpus (US Government, public domain): 2,124 and 1,653 pages. The first 1,000 pages of each, and then both whole.
        var directory = OutputDirectory();
        var files = directory is null ? null : Path.Combine(Path.GetDirectoryName(directory)!, "corpus", "files", "gov");
        var first = files is null ? null : Path.Combine(files, "BILLS-116hr133enr.pdf");
        var second = files is null ? null : Path.Combine(files, "BILLS-117hr2617enr.pdf");
        Assert.SkipWhen(first is null || !File.Exists(first) || !File.Exists(second), "the gov/ part of the corpus has not been fetched (mikepdf-render corpus fetch --tier core --filter gov/)");

        var a = File.ReadAllBytes(first!);
        var b = File.ReadAllBytes(second!);
        var sources = new[] { new AssemblerSource("BILLS-116hr133enr", a), new AssemblerSource("BILLS-117hr2617enr", b) };
        var options = new AssemblerOptions { WrapSourceOutlines = true };
        var set = PageAssembler.Open(sources, options);
        Assert.All(set.Sources, static s => Assert.True(s.IsUsable, s.Refusal?.ToString()));
        var pagesA = set.Sources[0].Info!.PageCount;
        var pagesB = set.Sources[1].Info!.PageCount;

        string Mb(long bytes) => (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture);
        var lines = new List<string>
        {
            $"merge of real documents from the corpus, on {Environment.ProcessorCount} logical processors, .NET {Environment.Version}, {DateTime.UtcNow:yyyy-MM-dd}",
            $"inputs: {Mb(a.Length)} MB ({pagesA} pages, tagged: {set.Sources[0].Info!.IsTagged}) + {Mb(b.Length)} MB ({pagesB} pages, tagged: {set.Sources[1].Info!.IsTagged})",
        };
        foreach (var (label, plan) in new[]
        {
            ("first 1,000 pages of each", new AssemblyPlan([.. Enumerable.Range(0, 1000).Select(static p => new SourcePage(0, p)), .. Enumerable.Range(0, 1000).Select(static p => new SourcePage(1, p))])),
            ("both whole", AssemblyPlan.Merge((0, pagesA), (1, pagesB))),
        })
        {
            var memory = Measure(() => PageAssembler.Open(sources, options).Assemble(plan, TestContext.Current.CancellationToken));
            Assert.True(memory.Result.IsSuccess, memory.Result.IsSuccess ? "" : memory.Result.Refusal.ToString());
            var output = memory.Result.Output;
            Assert.Equal(plan.Pages.Count, output.PageCount);
            AssemblyFixtures.CheckStructure(output);
            lines.Add($"{label}: {output.PageCount} pages, {Mb(output.BytesWritten)} MB, {output.ObjectCount} objects; open and assemble {memory.Elapsed.TotalMilliseconds:0} ms; allocated {Mb(memory.Allocated)} MB; working set grew by up to {Mb(memory.PeakGrowth)} MB; warnings: {string.Join(", ", output.Warnings.Select(static w => w.Code).Distinct())}");
        }

        lines.Add("a measurement, not a gate; one run each on a busy development machine");
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllLines(Path.Combine(directory, "assembler-merge-real-2x1000.txt"), lines);
        }

        foreach (var line in lines)
            TestContext.Current.SendDiagnosticMessage(line);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
    }

    private sealed record Measured<T>(T Result, TimeSpan Elapsed, long Allocated, long PeakGrowth);

    /// <summary>Runs <paramref name="work"/> after a full collection, sampling the process working set every few milliseconds meanwhile.</summary>
    private static Measured<T> Measure<T>(Func<T> work)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var self = Process.GetCurrentProcess();
        self.Refresh();
        var baseline = self.WorkingSet64;
        var peak = baseline;
        using var stop = new CancellationTokenSource();
        var sampling = new Thread(() =>
        {
            var sampled = Process.GetCurrentProcess();
            while (!stop.IsCancellationRequested)
            {
                sampled.Refresh();
                peak = Math.Max(peak, sampled.WorkingSet64);
                Thread.Sleep(2);
            }
        }) { IsBackground = true };
        sampling.Start();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var clock = Stopwatch.StartNew();
        var result = work();
        var elapsed = clock.Elapsed;
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        stop.Cancel();
        sampling.Join();
        return new Measured<T>(result, elapsed, allocated, peak - baseline);
    }
}
