#pragma warning disable CA1861 // test data: arrays written inline are the point of these cases

using System.Text;
using System.Text.Json;
using MikePDF.PdfWriter.Assembly;
using MikePDF.PdfWriter.Tests.Support;

namespace MikePDF.PdfWriter.Tests;

/// <summary>
/// The assembler on real files (PO-15, PO-27): the PDFium test resources checked into this project always, and every corpus file that has been fetched
/// (<c>mikepdf-render corpus fetch --tier smoke|core</c>; absent files are skipped, so a machine without the corpus runs the first part only). Each file is
/// copied whole and as a subset, the output is validated, reopened in a fresh PDFium and compared with the source: page count, text, sizes, rotation, and
/// that no link in the output leads anywhere its source link did not. A file the strict parser (or the page tree rules) refuses must come back as a typed
/// refusal; nothing may throw. The counts are written to <c>build/out/assembler-corpus-report.txt</c>.
/// </summary>
public sealed class AssemblerCorpusTests
{
    /// <summary>Files PDFium (the pinned build, in process) never finishes reading. The assembler is still run on them and its output validated; only the comparison with PDFium is skipped.</summary>
    private static readonly HashSet<string> PdfiumStalls = new(StringComparer.Ordinal)
    {
        "qpdf/shared-unnamed-field.pdf",
    };

    private const int MaxPages = 150;
    private const long MaxFileBytes = 30L << 20;

    private static string? RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MikePDF.slnx")))
                return dir.FullName;
        }

        return null;
    }

    private sealed record Outcome(string Name, string Verdict, string? Problem);

    /// <summary>PDFium is called in this process, so a file it cannot finish reading stalls the run; this names where (the file, and which step).</summary>
    private static void Progress(string step)
    {
        if (RepositoryRoot() is { } root && Directory.Exists(Path.Combine(root, "build", "out")))
            File.WriteAllText(Path.Combine(root, "build", "out", "assembler-corpus-progress.txt"), step);
    }

    /// <summary>Copies the first pages whole and every other page of them, and returns the verdict and what was wrong, if anything.</summary>
    private static Outcome Check(string name, byte[] bytes)
    {
        var set = PageAssembler.Open([new AssemblerSource(name, bytes)], AssemblyFixtures.Fixed);
        var status = set.Sources[0];
        var strict = StrictDocument.Open(bytes);
        if (!status.IsUsable)
        {
            var reason = status.Refusal!.Reason;
            // A refusal is typed. A file the strict parser refuses is refused for a parser reason (or a limit). The converse does not hold: the strict open reads only the
            // cross-reference chain, and the assembler also parses the catalog and the page tree, where an object can be what the parser refuses.
            var parserRefuses = strict.TryGetError(out var parserError);
            var fromParser = reason is AssemblyRefusalReason.SourceNotReadable or AssemblyRefusalReason.SourceEncrypted or AssemblyRefusalReason.SourceUnsupported;
            if (parserRefuses && !fromParser && reason != AssemblyRefusalReason.LimitExceeded)
                return new Outcome(name, "refused:" + reason, $"parser refuses ({parserError!.Code}) but the assembler says {status.Refusal}");
            return new Outcome(name, "refused:" + reason, null);
        }

        var pageCount = status.Info!.PageCount;
        var take = Math.Min(pageCount, MaxPages);
        var problems = new List<string>();
        PdfiumProfile? source = null;
        var sourceRead = false;
        foreach (var (label, plan) in new[]
        {
            ("first", AssemblyPlan.Range(0, 0, take - 1)),
            ("alternate", AssemblyPlan.Pick(0, Enumerable.Range(0, take).Where(static i => i % 2 == 0))),
        })
        {
            var result = set.Assemble(plan);
            if (!result.IsSuccess)
            {
                // The strict parser refuses an object the pages need (a stream without a usable /Length, say): a typed refusal, which is what R-39 asks for.
                var typed = result.Refusal.Reason is AssemblyRefusalReason.SourceNotReadable or AssemblyRefusalReason.SourceUnsupported or AssemblyRefusalReason.SourceInvalidStructure or AssemblyRefusalReason.LimitExceeded
                    && result.Refusal.SourceIndex == 0;
                return new Outcome(name, "assembly-refused:" + result.Refusal.Reason, typed ? null : $"{label}: {result.Refusal}");
            }

            var output = result.Output;
            var report = StructureValidator.Validate(output.Bytes.ToArray(), null, new ValidationOptions { DanglingReferencesAreErrors = true });
            if (!report.IsValid)
                problems.Add($"{label}: output invalid: {report.Issues.First(static i => i.Severity == ValidationSeverity.Error).Message}");

            if (PdfiumStalls.Contains(name))
                continue;
            if (!sourceRead)
            {
                Progress($"{name}: PDFium reads the source");
                source = Pdfium.Profile(bytes, render: false, maxPages: MaxPages);
                sourceRead = true;
            }

            Progress($"{name}: PDFium reads the {label} output");
            var copy = Pdfium.Profile(output.Bytes.ToArray(), render: false, maxPages: MaxPages);
            if (copy is null)
            {
                problems.Add($"{label}: PDFium refuses the output");
                continue;
            }

            if (copy.PageCount != output.PageCount)
                problems.Add($"{label}: PDFium reads {copy.PageCount} pages, {output.PageCount} were planned");
            if (source is null || source.PageCount != pageCount)
                continue; // PDFium reads the source differently (it repairs files the strict parser accepts): no page-by-page comparison
            var chosen = plan.Pages.Select(static p => p.Page).ToList();
            var position = chosen.Select(static (page, j) => (page, j)).ToDictionary(static x => x.page, static x => x.j);
            for (var i = 0; i < Math.Min(chosen.Count, copy.PageCount); i++)
            {
                var was = source.Pages[chosen[i]];
                var now = copy.Pages[i];
                if (was.Width == 0 && was.Height == 0)
                    continue; // PDFium could not load this page of the source (a damaged page); the assembler copied what the strict parser reads
                // PDFium reports a box only when it is set on the page itself; the assembler writes the inherited ones onto the page, so only a box the source page had is compared.
                if (was.Text != now.Text || was.Width != now.Width || was.Height != now.Height || was.Rotation != now.Rotation || (was.Media != "-" && was.Media != now.Media) || (was.Crop != "-" && was.Crop != now.Crop))
                {
                    problems.Add($"{label}: page {chosen[i] + 1} differs (text {was.Text == now.Text}, size {was.Width}x{was.Height} / {now.Width}x{now.Height}, rotation {was.Rotation} / {now.Rotation})");
                    break;
                }

                // A link may go, if it led to a page left out; it never leads somewhere new. (Targets without a marker are "page#<index>": the index in the source becomes the
                // position in the output.)
                var remaining = was.Links
                    .Select(l => l.StartsWith("page#", StringComparison.Ordinal) ? (int.TryParse(l.AsSpan(5), out var at) && position.TryGetValue(at, out var moved) ? "page#" + moved.ToString(System.Globalization.CultureInfo.InvariantCulture) : null) : l)
                    .OfType<string>()
                    .ToList();
                foreach (var target in now.Links)
                {
                    if (!remaining.Remove(target) && target is not "unresolved")
                    {
                        problems.Add($"{label}: page {chosen[i] + 1} has a link to {target} that its source page did not");
                        break;
                    }
                }
            }
        }

        return new Outcome(name, "assembled", problems.Count == 0 ? null : string.Join("; ", problems));
    }

    private static void Summarise(string title, List<Outcome> outcomes, string? reportName)
    {
        var lines = new List<string> { $"{title}: {outcomes.Count} files" };
        lines.AddRange(outcomes.GroupBy(static o => o.Verdict).OrderByDescending(static g => g.Count()).Select(static g => $"  {g.Key,-48} {g.Count()}"));
        var problems = outcomes.Where(static o => o.Problem is not null).ToList();
        lines.Add($"  problems: {problems.Count}");
        lines.AddRange(problems.Take(60).Select(static o => $"    {o.Name}: {o.Verdict}: {o.Problem}"));
        if (reportName is not null && RepositoryRoot() is { } root)
        {
            var path = Path.Combine(root, "build", "out", reportName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllLines(path, lines);
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, lines));
    }

    [Fact]
    [Trait("Spec", "PO-15")]
    [Trait("Spec", "PO-27")]
    public void EveryPdfiumFixtureInThisProjectIsCopiedWholeOrRefusedWithAReason()
    {
        Assert.SkipWhen(Pdfium.UnavailableReason is not null, Pdfium.UnavailableReason ?? "");
        var outcomes = FixtureTests.Expectations
            .Select(e => Check(e.File, FixtureTests.Read(e.File)))
            .Concat(new[] { "A.pdf", "B.pdf", "C.pdf" }.Select(f => Check(f, AssemblyFixtures.Read(f))))
            .ToList();
        Summarise("PDFium fixtures", outcomes, null);
        // The ones the strict parser accepts are assembled, not merely refused.
        Assert.True(outcomes.Count(static o => o.Verdict == "assembled") >= 14);
    }

    [Fact]
    [Trait("Spec", "PO-15")]
    [Trait("Spec", "PO-27")]
    public void EveryFetchedCorpusFileIsCopiedWholeOrRefusedWithAReason()
    {
        Assert.SkipWhen(Pdfium.UnavailableReason is not null, Pdfium.UnavailableReason ?? "");
        var root = RepositoryRoot();
        var files = Path.Combine(root ?? "", "build", "out", "corpus", "files");
        Assert.SkipWhen(root is null || !Directory.Exists(files), "the corpus has not been fetched (mikepdf-render corpus fetch --tier smoke)");

        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root!, "tests", "corpus", "manifest.json"), Encoding.UTF8));
        var outcomes = new List<Outcome>();
        foreach (var entry in manifest.RootElement.GetProperty("files").EnumerateArray())
        {
            var id = entry.GetProperty("id").GetString()!;
            var expect = entry.GetProperty("expect");
            // The two files that take the engine down on purpose would take this process down too; encrypted files with a password are refused by design.
            if (expect.TryGetProperty("engineEvent", out _) || expect.TryGetProperty("password", out _))
                continue;
            var path = Path.Combine(files, id.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path) || new FileInfo(path).Length > MaxFileBytes)
                continue;
            if (Environment.GetEnvironmentVariable("MIKEPDF_CORPUS_FILTER") is { Length: > 0 } only && !id.Contains(only, StringComparison.Ordinal))
                continue;
            Progress(id + ": assembling");
            outcomes.Add(Check(id, File.ReadAllBytes(path)));
        }

        Assert.SkipWhen(outcomes.Count == 0, "no corpus file is on disk");
        Summarise("corpus", outcomes, "assembler-corpus-report.txt");
    }
}
