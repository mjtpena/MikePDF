using Quire.PdfWriter.Tests.Support;

namespace Quire.PdfWriter.Tests;

/// <summary>
/// R-31 differential checks against PDFium (test-only P/Invoke; Quire.PdfWriter itself never references PDFium).
///
/// The security-relevant direction is: whatever the strict parser accepts, PDFium must read the same way. So every strictly
/// opened file and every incremental output must open in PDFium with the same page count. The converse is allowed to differ:
/// strict mode refuses files PDFium recovers from, by design.
/// </summary>
public sealed class PdfiumDifferentialTests
{
    private static void RequirePdfium() => Assert.SkipWhen(Pdfium.UnavailableReason is not null, Pdfium.UnavailableReason ?? "");

    private static Pdfium.Info Opens(byte[] bytes, string what)
    {
        var info = Pdfium.Open(bytes);
        Assert.True(info is not null, $"PDFium refused {what}");
        return info!;
    }

    [Theory]
    [MemberData(nameof(DocumentTests.Layouts), MemberType = typeof(DocumentTests))]
    public void GeneratedLayoutsAgreeBeforeAndAfterUpdates(string layout)
    {
        RequirePdfium();
        byte[] original = DocumentTests.Build(layout).Bytes;
        var doc = DocInfo.Strict(original);
        var before = Opens(original, layout);
        Assert.Equal(DocInfo.PageCount(doc), before.PageCount);
        Assert.Equal(DocInfo.AnnotCount(doc, 0), before.Page0AnnotCount);

        byte[] current = original;
        int added = 0;
        for (int round = 1; round <= 3; round++)
        {
            var cur = DocInfo.Strict(current);
            current = DocInfo.AppendAnnotations(cur, round);
            added += round;

            var after = DocInfo.Strict(current);
            var pdfium = Opens(current, $"{layout} after update {round}");
            Assert.Equal(DocInfo.PageCount(after), pdfium.PageCount);
            Assert.Equal(before.PageCount, pdfium.PageCount);
            Assert.Equal(DocInfo.AnnotCount(after, 0), pdfium.Page0AnnotCount); // PDFium sees exactly the annotations the writer wrote
            Assert.Equal(before.Page0AnnotCount + added, pdfium.Page0AnnotCount);
        }
    }

    [Theory]
    [MemberData(nameof(FixtureTests.Openable), MemberType = typeof(FixtureTests))]
    public void FixturesAgreeBeforeAndAfterAnUpdate(string name)
    {
        RequirePdfium();
        var expectation = FixtureTests.Expectations.Single(e => e.File == name);
        byte[] original = FixtureTests.Read(name);
        var doc = DocInfo.Strict(original);

        var before = Opens(original, name);
        Assert.Equal(expectation.Pages, before.PageCount);
        Assert.Equal(DocInfo.PageCount(doc), before.PageCount);

        byte[] updated = DocInfo.AppendAnnotations(doc, 2);
        var after = Opens(updated, name + " (updated)");
        Assert.Equal(before.PageCount, after.PageCount);
        Assert.Equal(before.Page0AnnotCount + 2, after.Page0AnnotCount);
    }

    [Theory]
    [MemberData(nameof(FixtureTests.Names), MemberType = typeof(FixtureTests))]
    public void StrictIsNeverLooserThanPdfiumOnFixtures(string name)
    {
        RequirePdfium();
        var expectation = FixtureTests.Expectations.Single(e => e.File == name);
        var pdfium = Pdfium.Open(FixtureTests.Read(name));

        // Whatever PDFium cannot open, strict must not accept.
        if (pdfium is null)
            Assert.NotNull(expectation.StrictError);
        else
            Assert.Equal(expectation.Pages, pdfium.PageCount);
    }

    [Fact]
    public void ObjectsReplacedByAnUpdateAreWhatPdfiumReads()
    {
        RequirePdfium();

        // Replace the page tree's /Count-less page 1 media box and check PDFium's page count follows the new revision, not the old.
        byte[] original = PdfBuilder.XrefStream().Bytes;
        var doc = DocInfo.Strict(original);
        var update = new IncrementalUpdate(doc);
        var pages = (PdfDictionary)doc.GetObject(2).Value;
        var kids = (PdfArray)pages["Kids"]!;

        // Drop the second page from the tree.
        Assert.True(update.Replace(2, pages.With("Kids", new PdfArray([kids[0]])).With("Count", new PdfInteger(1))).IsSuccess);
        byte[] shrunk = update.Build().Value;
        Assert.Equal(2, Opens(original, "original").PageCount);
        Assert.Equal(1, Opens(shrunk, "shrunk").PageCount);
        Assert.Equal(1, DocInfo.PageCount(DocInfo.Strict(shrunk)));
    }

    [Fact]
    public void MutatedFilesThatStrictModeCallsValidAreAlwaysOpenedByPdfium()
    {
        RequirePdfium();
        var seeds = Seeds.Build();
        var rng = new Random(20261029);
        int checkedFiles = 0;
        var disagreements = new List<string>();
        bool deep = Environment.GetEnvironmentVariable("QUIRE_DIFFERENTIAL_DEEP") == "1";
        var deepNotes = new List<string>();

        // Only the file-level property: strict + validator accept => PDFium opens it. Page counts are compared separately for
        // unmutated files, because a mutation of /Count (say) legitimately changes what each reader reports.
        int iterations = int.TryParse(Environment.GetEnvironmentVariable("QUIRE_DIFFERENTIAL_ITERATIONS"), out int n) ? n : 6000; // raise locally for a soak
        for (int i = 0; i < iterations; i++)
        {
            var (name, seed) = seeds[rng.Next(seeds.Count)];
            byte[] mutant = Mutator.Mutate(rng, seed);
            if (!StrictDocument.Open(mutant).TryGetValue(out var doc) || !StructureValidator.Validate(doc).IsValid)
                continue;
            checkedFiles++;
            var info = Pdfium.Open(mutant);
            if (deep && info is not null && (info.PageCount != DocInfo.PageCount(doc) || info.Page0AnnotCount != DocInfo.AnnotCount(doc, 0)))
                deepNotes.Add($"#{i} from {name}: pdfium pages={info.PageCount} annots={info.Page0AnnotCount}; strict pages={DocInfo.PageCount(doc)} annots={DocInfo.AnnotCount(doc, 0)}");
            if (info is null)
            {
                string path = Path.Combine(Path.GetTempPath(), $"quire-differential-{i}.pdf");
                File.WriteAllBytes(path, mutant);
                disagreements.Add($"#{i} from {name}: saved to {path}");
            }
        }

        if (deep)
            File.WriteAllLines(Path.Combine(Path.GetTempPath(), "quire-differential-deep.txt"), deepNotes);
        Assert.True(checkedFiles > 200, $"only {checkedFiles} mutants were strict-valid");
        Assert.True(disagreements.Count == 0, "strict+valid but PDFium refuses:\n" + string.Join("\n", disagreements));
    }
}
