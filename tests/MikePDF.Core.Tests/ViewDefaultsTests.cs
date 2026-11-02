using MikePDF.Core.Layout;
using MikePDF.Core.Persistence;
using MikePDF.Core.Settings;

namespace MikePDF.Core.Tests;

/// <summary>The settings view.defaultArrangement and view.defaultFlow (docs/features/settings-policies.md, page modes): how a document with no record of its own is laid out.</summary>
public sealed class ViewDefaultsTests
{
    private static SettingDefinition Setting(string key) => SettingsRegistry.All.Single(s => s.Key == key);

    [Fact]
    public void TheDefaultsAreTheLayoutTheAppHasAlwaysHad()
    {
        Assert.Equal("OnePage", Setting("view.defaultArrangement").Default);
        Assert.Equal("Continuous", Setting("view.defaultFlow").Default);
        Assert.Equal(ViewMode.Default, ViewMode.Parse((string?)Setting("view.defaultArrangement").Default, (string?)Setting("view.defaultFlow").Default, null));
    }

    [Fact]
    public void EveryValueOfTheSettingsNamesAModeAndEveryModeHasAValue()
    {
        var arrangements = Setting("view.defaultArrangement").Values!;
        var flows = Setting("view.defaultFlow").Values!;
        Assert.Equal(Enum.GetNames<PageArrangement>().Order(StringComparer.Ordinal), arrangements.Order(StringComparer.Ordinal));
        Assert.Equal(Enum.GetNames<PageFlow>().Order(StringComparer.Ordinal), flows.Order(StringComparer.Ordinal));
        foreach (var a in arrangements)
        {
            foreach (var f in flows)
            {
                var mode = ViewMode.Parse(a, f, null);
                Assert.Equal(a, mode.Arrangement.ToString());
                Assert.Equal(f, mode.Flow.ToString());
            }
        }
    }

    [Fact]
    public void TheStoreKeepsTheDefaultModeButNeverADirectionWithIt()
    {
        var path = Path.Combine(Path.GetTempPath(), "mikepdf-defaults-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new DocumentStore(path);
            Assert.Equal(ViewMode.Default, store.DefaultMode);
            store.DefaultMode = new ViewMode(PageArrangement.FacingWithCover, PageFlow.Paged, ReadingDirection.RightToLeft);
            Assert.Equal(new ViewMode(PageArrangement.FacingWithCover, PageFlow.Paged, ReadingDirection.Auto), store.DefaultMode);

            // A document with a record keeps its own mode; the default is only for the ones with none.
            store.SavePosition(@"C:\docs\mine.pdf", 3, 0, 1, FitMode.None, 0, new ViewMode(PageArrangement.Facing));
            Assert.Equal(PageArrangement.Facing, store.GetPosition(@"C:\docs\mine.pdf")!.Mode.Arrangement);
            Assert.Null(store.GetPosition(@"C:\docs\new.pdf"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ThePageLayoutOfAFileIsNotReadBecauseTheEngineDoesNotSayIt()
    {
        // /PageLayout (OQ-PV-5) would need a schema change; until then nothing in the protocol carries it, and nothing guesses at it from the file outside the sandbox.
        var schema = File.ReadAllText(Path.Combine(ResourceText.RepoRoot(), "src", "MikePDF.Protocol", "schema", "mikepdf.protocol.json"));
        Assert.DoesNotContain("pageLayout", schema, StringComparison.OrdinalIgnoreCase);
    }
}
