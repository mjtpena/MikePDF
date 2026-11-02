using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MikePDF.Perf.Documents;

namespace MikePDF.Perf.Tests;

public sealed partial class PerfDocumentTests
{
    [Fact]
    public void TheSameSpecGivesTheSameBytesEveryTime()
    {
        foreach (var spec in PerfDocuments.All)
        {
            Assert.True(PerfDocuments.Build(spec).AsSpan().SequenceEqual(PerfDocuments.Build(spec)), spec.Id);
        }
    }

    [Fact]
    public void TheTwentyPageDocumentIsByteForByteWhatWasPinned()
    {
        // If this fails the generator changed, and every stored baseline was measured on a different document: regenerate the baselines too.
        var hash = Convert.ToHexStringLower(SHA256.HashData(PerfDocuments.Build(PerfDocuments.Text20)));
        Assert.Equal("0733a3d9c5c96947721d346e7ec840ee6db2f86f7afb1ad501b0b14dd293a4cf", hash);
    }

    [Fact]
    public void TheStandardDocumentsHaveTheBudgetedPageCounts()
    {
        Assert.Equal([1, 20, 500, 2000], PerfDocuments.All.Select(s => s.Pages));
        foreach (var spec in PerfDocuments.All)
        {
            var text = Encoding.ASCII.GetString(PerfDocuments.Build(spec));
            Assert.Equal(spec.Pages, PageObject().Count(text));
            Assert.Contains($"/Count {spec.Pages.ToString(CultureInfo.InvariantCulture)} /Kids", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryCrossReferenceEntryPointsAtItsObject()
    {
        foreach (var spec in new[] { PerfDocuments.Text1, PerfDocuments.Text20, PerfDocuments.Text500 })
        {
            var bytes = PerfDocuments.Build(spec);
            var text = Encoding.ASCII.GetString(bytes);
            var startXref = int.Parse(Regex.Match(text, @"startxref\n(\d+)\n%%EOF").Groups[1].Value, CultureInfo.InvariantCulture);
            Assert.StartsWith("xref\n0 ", text[startXref..], StringComparison.Ordinal);
            var entries = Regex.Matches(text[startXref..], @"(\d{10}) 00000 n \n");
            Assert.NotEmpty(entries);
            for (var i = 0; i < entries.Count; i++)
            {
                var offset = int.Parse(entries[i].Groups[1].Value, CultureInfo.InvariantCulture);
                Assert.StartsWith($"{i + 1} 0 obj\n", text[offset..], StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void TheNeedleIsWhereTheSpecPutsItAndNowhereElse()
    {
        var spec = PerfDocuments.Text500;
        Assert.Equal(11, spec.ExpectedNeedleMatches);
        Assert.Equal(4, spec.FirstNeedlePage);
        var found = new Dictionary<int, int>();
        for (var page = 0; page < spec.Pages; page++)
        {
            var count = PerfDocuments.PageLines(spec, page).Sum(line => Regex.Count(line, PerfDocuments.Needle, RegexOptions.None, TimeSpan.FromSeconds(5)));
            if (count > 0)
            {
                found[page] = count;
            }
        }

        Assert.Equal(spec.Needles.OrderBy(p => p.Key), found.OrderBy(p => p.Key));
        Assert.Equal(2, found[4]);
        Assert.Equal(1, found[54]);
        foreach (var other in new[] { PerfDocuments.Text1, PerfDocuments.Text20, PerfDocuments.Text2000 })
        {
            Assert.Equal(0, other.ExpectedNeedleMatches);
            Assert.DoesNotContain(PerfDocuments.Needle, Encoding.ASCII.GetString(PerfDocuments.Build(other)), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheBytesHoldTheNeedleAsManyTimesAsTheSpecCounts()
    {
        var text = Encoding.ASCII.GetString(PerfDocuments.Build(PerfDocuments.Text500));
        Assert.Equal(PerfDocuments.Text500.ExpectedNeedleMatches, Regex.Count(text, PerfDocuments.Needle, RegexOptions.None, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void ADocumentWithAPageTreeKeepsEveryPageUnderItsNode()
    {
        var text = Encoding.ASCII.GetString(PerfDocuments.Build(PerfDocuments.Text2000));
        // 2000 pages in nodes of 50: 40 intermediate Pages objects besides the root.
        Assert.Equal(41, Regex.Count(text, @"/Type /Pages", RegexOptions.None, TimeSpan.FromSeconds(5)));
        Assert.Equal(40, Regex.Count(text, @"/Type /Pages /Parent 2 0 R /Count 50 ", RegexOptions.None, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void ASpecThatCannotBeBuiltIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PerfDocuments.Build(new DocumentSpec("none", 0, 1, new Dictionary<int, int>())));
        Assert.Throws<ArgumentOutOfRangeException>(() => PerfDocuments.Build(new DocumentSpec("off", 3, 1, new Dictionary<int, int> { [3] = 1 })));
    }

    [Fact]
    public void MaterializeWritesMissingFilesRepairsStaleOnesAndLeavesGoodOnesAlone()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mikepdf-perf-tests-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var first = PerfDocuments.Materialize(directory, [PerfDocuments.Text1, PerfDocuments.Text20]);
            Assert.Equal(2, first.Count);
            var path = Path.Combine(directory, "text1.pdf");
            var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(path, stamp);
            PerfDocuments.Materialize(directory, [PerfDocuments.Text1]);
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(path)); // untouched

            File.WriteAllText(path, "not a pdf");
            var repaired = PerfDocuments.Materialize(directory, [PerfDocuments.Text1]);
            Assert.Equal(repaired[0].Sha256, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
            Assert.Equal(first[0].Sha256, repaired[0].Sha256);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [GeneratedRegex(@"/Type /Page /Parent \d+ 0 R /Contents \d+ 0 R", RegexOptions.None, 5000)]
    private static partial Regex PageObject();
}
