using System.Text;
using System.Text.Json;
using MikePDF.Core.Colour;
using MikePDF.Core.Persistence;
using MikePDF.Core.Settings;

namespace MikePDF.Core.Tests;

/// <summary>The settings store and registry (docs/features/settings-policies.md): SET-01 to SET-06, SET-19, SET-27, and the migration of the old store.</summary>
public sealed class SettingsTests
{
    // ---- SET-01 ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void SET01_EverySettingHasAUniqueKeyAValidDefaultAnOwnerAndItsStrings()
    {
        var keys = SettingsRegistry.All.Select(s => s.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        foreach (var setting in SettingsRegistry.All)
        {
            Assert.Matches(@"^[a-z][A-Za-z0-9]*(\.[a-z][A-Za-z0-9]*)+$", setting.Key);
            Assert.True(setting.IsValid(setting.Default), setting.Key + " does not satisfy its own rules");
            Assert.False(string.IsNullOrWhiteSpace(setting.Owner));
            if (setting.Kind == SettingKind.Enum)
            {
                Assert.NotEmpty(setting.Values!);
                Assert.Equal(setting.Values!.Count, setting.Values.Distinct().Count());
                foreach (var value in setting.Values)
                {
                    Assert.True(ResourceText.Has(setting.ValueKey(value)), "No words for " + setting.Key + " = " + value);
                }
            }

            if (setting.Kind == SettingKind.Number)
            {
                Assert.True(setting.Min <= setting.Max);
            }

            if (setting.Section != SettingSection.None)
            {
                Assert.True(ResourceText.Has(setting.NameKey), setting.NameKey);
                Assert.True(ResourceText.Has(setting.DescriptionKey), setting.DescriptionKey);
            }

            if (setting.PolicyName is { } policy)
            {
                var definition = PolicyCatalog.Find(policy);
                Assert.NotNull(definition);
                Assert.Equal(setting.Key, definition.SettingKey); // the lock goes both ways
            }
        }

        foreach (var policy in PolicyCatalog.All.Where(p => p.SettingKey is not null))
        {
            var setting = SettingsRegistry.Find(policy.SettingKey!);
            Assert.NotNull(setting);
            Assert.Equal(policy.Name, setting.PolicyName);
        }
    }

    [Fact]
    public void SET01_ValuesOutsideTheRulesAreNotValid()
    {
        var recent = SettingsRegistry.Find("recent.maxItems")!;
        Assert.True(recent.IsValid(0));
        Assert.True(recent.IsValid(50));
        Assert.False(recent.IsValid(51));
        Assert.False(recent.IsValid(-1));
        Assert.False(recent.IsValid("20"));
        Assert.False(recent.IsValid(20.5));
        var theme = SettingsRegistry.Find("general.theme")!;
        Assert.False(theme.IsValid("Neon"));
        Assert.False(theme.IsValid("dark")); // case matters: the value is what is stored
        Assert.False(theme.IsValid(1));
    }

    // ---- SET-02 and SET-03 -------------------------------------------------------------------------------------------------------------

    public static TheoryData<string, string, int, string, int, string> Matrix => new()
    {
        // setting, policy, valid policy value, its effect on the setting, an invalid value, what an invalid policy does ("locked:<v>" or "free")
        { "links.behaviour", "ExternalLinks", 2, "Confirm", 9, "locked:Block" },
        { "save.mode", "SaveMode", 1, "AlwaysSaveAsCopy", 5, "free" },
        { "engines.maxLiveTabs", "MaxLiveTabs", 3, "3", 99, "free" },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public void SET02_EachSettingWithAPolicyGivesTheRightValueSourceAndLockInAllSixCases(string key, string policy, int validValue, string effect, int invalidValue, string invalidResult)
    {
        var definition = SettingsRegistry.Find(key)!;
        object UserChoice() => definition.Kind == SettingKind.Number ? 7 : definition.Values!.First(v => v != (string)definition.Default);
        foreach (var userSets in new[] { false, true })
        {
            // policy absent
            using (var fixture = new SettingsFixture())
            {
                using var store = fixture.Open();
                var choice = UserChoice();
                if (userSets)
                {
                    Assert.Equal(SetResult.Applied, store.Set(key, choice));
                }

                var effective = store.Effective(key);
                Assert.False(effective.Locked);
                Assert.Equal(userSets ? SettingSource.User : SettingSource.Default, effective.Source);
                Assert.Equal(userSets ? choice : definition.Default, effective.Value);
            }

            // policy valid
            using (var fixture = new SettingsFixture())
            {
                using var store = fixture.Open();
                var choice = UserChoice();
                if (userSets)
                {
                    store.Set(key, choice);
                }

                fixture.SetPolicy(PolicyScope.Machine, SettingsFixture.Dword(policy, validValue));
                var effective = store.Effective(key);
                Assert.True(effective.Locked);
                Assert.Equal(SettingSource.Policy, effective.Source);
                Assert.Equal(effect, effective.Text);
                Assert.Equal(policy, effective.PolicyName);
                Assert.Equal(SetResult.Locked, store.Set(key, choice)); // the person can't change it
            }

            // policy invalid
            using (var fixture = new SettingsFixture())
            {
                using var store = fixture.Open();
                var choice = UserChoice();
                if (userSets)
                {
                    store.Set(key, choice);
                }

                fixture.SetPolicy(PolicyScope.Machine, SettingsFixture.Dword(policy, invalidValue));
                var effective = store.Effective(key);
                if (invalidResult.StartsWith("locked:", StringComparison.Ordinal))
                {
                    Assert.True(effective.Locked);
                    Assert.Equal(SettingSource.Policy, effective.Source);
                    Assert.Equal(invalidResult["locked:".Length..], effective.Text);
                }
                else
                {
                    Assert.False(effective.Locked); // ignored: as if the policy were not there
                    Assert.Equal(userSets ? SettingSource.User : SettingSource.Default, effective.Source);
                    Assert.Equal(userSets ? choice : definition.Default, effective.Value);
                }
            }
        }
    }

    [Fact]
    public void SET02_AStringPolicyLocksTheAuthorNameAndAnEmptyOneIsIgnored()
    {
        using var fixture = new SettingsFixture();
        using var store = fixture.Open();
        store.Set("markup.authorName", "Pat");
        fixture.SetPolicy(PolicyScope.Machine, SettingsFixture.Text("AnnotationAuthorName", "  Directory Name  "));
        Assert.Equal(("Directory Name", true, SettingSource.Policy), (store.Effective("markup.authorName").Text, store.Effective("markup.authorName").Locked, store.Effective("markup.authorName").Source));
        fixture.SetPolicy(PolicyScope.Machine, SettingsFixture.Text("AnnotationAuthorName", "   "));
        Assert.Equal(("Pat", false), (store.Effective("markup.authorName").Text, store.Effective("markup.authorName").Locked));
        fixture.SetPolicy(PolicyScope.Machine, SettingsFixture.Dword("AnnotationAuthorName", 4)); // the wrong type
        Assert.False(store.Effective("markup.authorName").Locked);
    }

    [Theory]
    [InlineData("JavaScriptEnabled", 0)]
    [InlineData("ExternalLinks", 0)]
    [InlineData("AiMode", 0)]
    [InlineData("TelemetryEnabled", 0)]
    [InlineData("ShellThumbnailsEnabled", 0)]
    [InlineData("ShellPreviewEnabled", 0)]
    [InlineData("SettingsImportExportEnabled", 0)]
    public void SET03_FailClosedPoliciesGiveTheRestrictiveResultForWrongTypedOutOfRangeAndUnreadableValues(string name, int restrictive)
    {
        var bad = new[]
        {
            new RawPolicyValue(name, RawPolicyKind.Text, Text: "1"),
            new RawPolicyValue(name, RawPolicyKind.Dword, 77),
            new RawPolicyValue(name, RawPolicyKind.Dword, -1),
            new RawPolicyValue(name, RawPolicyKind.TextList, Lines: ["1"]),
            new RawPolicyValue(name, RawPolicyKind.Other),
            new RawPolicyValue(name, RawPolicyKind.Dword, 1, TooLarge: true),
        };
        foreach (var value in bad)
        {
            var source = new FixedPolicySource();
            source.Set(PolicyScope.Machine, value);
            var entry = new PolicyProvider(source).Current[name];
            Assert.True(entry.IsConfigured, name + " " + value);
            Assert.Equal(restrictive, entry.Value);
            Assert.Equal(ObservationOutcome.InvalidClosed, entry.Observations.Single().Outcome);
        }

        var unreadable = new FixedPolicySource();
        unreadable.Failing.Add(PolicyScope.Machine);
        var failed = new PolicyProvider(unreadable).Current[name];
        Assert.Equal(PolicyState.ReadFailed, failed.State);
        Assert.True(failed.IsConfigured);
        Assert.Equal(restrictive, failed.Value);
    }

    [Fact]
    public void SET03_ThePoliciesThatAreNotFailClosedIgnoreABadValueAndAnUnreadableHive()
    {
        foreach (var name in new[] { "SaveMode", "EngineMemoryLimitMB", "MaxLiveTabs", "PrintingEnabled", "AllowedLinkSchemes", "AnnotationAuthorName" })
        {
            var source = new FixedPolicySource();
            source.Set(PolicyScope.Machine, new RawPolicyValue(name, RawPolicyKind.Other));
            var entry = new PolicyProvider(source).Current[name];
            Assert.False(entry.IsConfigured, name);
            Assert.Equal(ObservationOutcome.Invalid, entry.Observations.Single().Outcome);
            source.Failing.Add(PolicyScope.Machine);
            Assert.False(new PolicyProvider(source).Current[name].IsConfigured, name);
        }

        Assert.Equal(2048, new PolicyProvider(new FixedPolicySource()).Current.EngineMemoryLimitMb);
    }

    // ---- SET-04, SET-05 ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void SET04_TheFileRoundTripsAndUnknownKeysSurviveARewrite()
    {
        using var fixture = new SettingsFixture();
        File.WriteAllText(fixture.SettingsPath, """{ "schemaVersion": 3, "values": { "general.theme": "Dark", "future.thing": { "a": [1, 2, 3] }, "future.flag": true }, "keybindings": [] }""");
        using (var store = fixture.Open())
        {
            Assert.Equal("Dark", store.Effective("general.theme").Text);
            Assert.Equal(SetResult.Applied, store.Set("recent.maxItems", 12));
            store.Set("view.pageColour", "Sepia");
            Assert.True(store.Flush());
        }

        using var again = fixture.Open();
        Assert.Equal(("Dark", 12, "Sepia"), (again.Value<string>("general.theme"), again.Value<int>("recent.maxItems"), again.Value<string>("view.pageColour")));
        var text = File.ReadAllText(fixture.SettingsPath);
        using var document = JsonDocument.Parse(text);
        var values = document.RootElement.GetProperty("values");
        Assert.Equal("[1,2,3]", values.GetProperty("future.thing").GetProperty("a").GetRawText().Replace(" ", string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty));
        Assert.True(values.GetProperty("future.flag").GetBoolean());
        Assert.Equal(3, document.RootElement.GetProperty("schemaVersion").GetInt32()); // a newer version's number is kept
    }

    [Fact]
    public void SET04_AWriteIsAtomicAndAHalfWrittenTempFileNeverReplacesTheSettings()
    {
        using var fixture = new SettingsFixture();
        using (var store = fixture.Open())
        {
            store.Set("general.theme", "Light");
            store.Flush();
        }

        // The writer was killed after it began the temp file and before it moved it into place.
        File.WriteAllText(fixture.SettingsPath + ".tmp", """{ "schemaVersion": 1, "values": { "general.theme": "Dar""");
        using var again = fixture.Open();
        Assert.Equal("Light", again.Value<string>("general.theme"));
        Assert.False(again.TakeCorruptNotice());
        again.Set("general.theme", "Dark");
        Assert.True(again.Flush());
        Assert.False(File.Exists(fixture.SettingsPath + ".tmp"));
        using var last = fixture.Open();
        Assert.Equal("Dark", last.Value<string>("general.theme"));
    }

    [Fact]
    public void SET04_TheKeyCapAndTheSizeAndDepthCapsHold()
    {
        using var fixture = new SettingsFixture();
        var many = new StringBuilder("{\"schemaVersion\":1,\"values\":{\"general.theme\":\"Dark\"");
        for (var i = 0; i < 400; i++)
        {
            many.Append(",\"unknown.key").Append(i).Append("\":").Append(i);
        }

        File.WriteAllText(fixture.SettingsPath, many.Append("}}").ToString());
        using (var store = fixture.Open())
        {
            store.Set("recent.maxItems", 5);
            store.Flush();
        }

        using var document = JsonDocument.Parse(File.ReadAllText(fixture.SettingsPath));
        Assert.True(document.RootElement.GetProperty("values").EnumerateObject().Count() <= SettingsStore.MaxKeys);

        File.WriteAllText(fixture.SettingsPath, "{\"schemaVersion\":1,\"values\":{\"general.theme\":\"Dark\"}, \"x\":" + new string('[', 20) + new string(']', 20) + "}");
        using var deep = fixture.Open();
        Assert.Equal("System", deep.Value<string>("general.theme")); // nested past 8 levels: treated as damaged
        Assert.True(deep.TakeCorruptNotice());
    }

    public static TheoryData<string> Damaged => new()
    {
        string.Empty,
        "{",
        "not json at all",
        "[1, 2, 3]",
        "{\"schemaVersion\": 1, \"values\": {\"general.theme\": \"Dar",
        "\uFEFF{\"schemaVersion\": 1, \"values\": {\"general.theme\": \"Dark\"}}",
        "{\"schemaVersion\": 1, // a comment\n \"values\": {}}",
    };

    [Theory]
    [MemberData(nameof(Damaged))]
    public void SET05_AFileThatCannotBeReadGivesDefaultsKeepsACopyAndSaysSoOnce(string content)
    {
        using var fixture = new SettingsFixture();
        File.WriteAllText(fixture.SettingsPath, content, new UTF8Encoding(false));
        using var store = fixture.Open();
        Assert.Equal("System", store.Value<string>("general.theme"));
        Assert.True(store.FileExisted);
        Assert.True(store.TakeCorruptNotice());
        Assert.False(store.TakeCorruptNotice());
        Assert.Single(Directory.GetFiles(fixture.Directory, "settings.corrupt-*.json"));
        Assert.False(File.Exists(fixture.SettingsPath));
        Assert.Equal(SetResult.Applied, store.Set("general.theme", "Dark")); // the store keeps working
        Assert.True(store.Flush());
    }

    [Fact]
    public void SET05_AHugeFileIsSetAsideWithoutBeingParsed()
    {
        using var fixture = new SettingsFixture();
        File.WriteAllText(fixture.SettingsPath, "{\"schemaVersion\":1,\"values\":{},\"pad\":\"" + new string('x', 2 * 1024 * 1024) + "\"}");
        using var store = fixture.Open();
        Assert.True(store.TakeCorruptNotice());
        Assert.Single(Directory.GetFiles(fixture.Directory, "settings.corrupt-*.json"));
    }

    [Fact]
    public void SET05_OnlyTheLastThreeDamagedCopiesAreKept()
    {
        using var fixture = new SettingsFixture();
        for (var i = 0; i < 5; i++)
        {
            File.WriteAllText(fixture.SettingsPath, "{ broken " + i);
            using var store = fixture.Open();
            Assert.True(store.TakeCorruptNotice());
        }

        var kept = Directory.GetFiles(fixture.Directory, "settings.corrupt-*.json");
        Assert.Equal(SettingsStore.MaxKeepCorrupt, kept.Length);
        Assert.DoesNotContain(kept, f => File.ReadAllText(f).Contains("broken 0", StringComparison.Ordinal) || File.ReadAllText(f).Contains("broken 1", StringComparison.Ordinal));
    }

    [Fact]
    public void SET05_OneBadValueNeverResetsTheOthers()
    {
        using var fixture = new SettingsFixture();
        File.WriteAllText(fixture.SettingsPath, """
            { "schemaVersion": 1, "values": {
              "general.theme": "Dark", "view.pageColour": "Neon", "recent.maxItems": 99, "view.restorePosition": "yes",
              "links.behaviour": "Block", "engines.maxLiveTabs": 4.5 }, "keybindings": "oops" }
            """);
        using var store = fixture.Open();
        Assert.False(store.TakeCorruptNotice()); // parsed fine: only values were wrong
        Assert.Equal("Dark", store.Value<string>("general.theme"));
        Assert.Equal("Normal", store.Value<string>("view.pageColour"));
        Assert.Equal(20, store.Value<int>("recent.maxItems"));
        Assert.True(store.Value<bool>("view.restorePosition"));
        Assert.Equal("Block", store.Value<string>("links.behaviour"));
        Assert.Equal(6, store.Value<int>("engines.maxLiveTabs"));
        Assert.Empty(store.Keybindings);
    }

    [Fact]
    public void SET05_AMissingFileMeansDefaultsAndNoNotice()
    {
        using var fixture = new SettingsFixture();
        using var store = fixture.Open();
        Assert.False(store.FileExisted);
        Assert.False(store.TakeCorruptNotice());
        Assert.All(SettingsRegistry.All, s => Assert.Equal(SettingSource.Default, store.Effective(s.Key).Source));
    }

    // ---- SET-06 ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void SET06_AChangeRaisesOneEventWithItsKeyAndANoOpWriteRaisesNone()
    {
        using var fixture = new SettingsFixture();
        using var store = fixture.Open();
        var firstWindow = new List<string[]>();
        var secondWindow = new List<string[]>();
        store.SettingsChanged += keys => firstWindow.Add([.. keys]);
        store.SettingsChanged += keys => secondWindow.Add([.. keys]);

        Assert.Equal(SetResult.Applied, store.Set("general.theme", "Dark"));
        Assert.Equal(SetResult.Unchanged, store.Set("general.theme", "Dark"));
        Assert.Equal(SetResult.Unchanged, store.Set("recent.maxItems", 20)); // the default, never set
        Assert.Equal(SetResult.Invalid, store.Set("recent.maxItems", 500));
        Assert.Equal(SetResult.UnknownKey, store.Set("no.such", 1));
        Assert.Equal(SetResult.Applied, store.Set("recent.maxItems", 5));

        Assert.Equal([["general.theme"], ["recent.maxItems"]], firstWindow);
        Assert.Equal(firstWindow, secondWindow);
    }

    [Fact]
    public void SET06_APolicyThatChangesWhileTheAppRunsRaisesTheKeysItChanged()
    {
        using var fixture = new SettingsFixture();
        using var store = fixture.Open();
        var events = new List<string[]>();
        store.SettingsChanged += keys => events.Add([.. keys]);

        fixture.SetPolicy(PolicyScope.Machine, SettingsFixture.Dword("ExternalLinks", 0));
        Assert.Equal([["links.behaviour"]], events);
        Assert.Equal("Block", store.Effective("links.behaviour").Text);

        fixture.SetPolicy(PolicyScope.Machine, SettingsFixture.Dword("JavaScriptEnabled", 0)); // no setting depends on it
        Assert.Single(events);

        fixture.ClearPolicies();
        Assert.Equal(2, events.Count);
        Assert.Equal("ShowAddress", store.Effective("links.behaviour").Text);
    }

    [Fact]
    public void SET06_TheStoreWritesAfterTheDelayAndFlushWritesAtOnce()
    {
        using var fixture = new SettingsFixture();
        using var store = fixture.Open();
        store.Set("general.theme", "Dark");
        Assert.False(File.Exists(fixture.SettingsPath)); // debounced: not yet
        Assert.True(store.Flush());
        Assert.True(File.Exists(fixture.SettingsPath));
        File.Delete(fixture.SettingsPath);
        Assert.True(store.Flush()); // nothing pending: nothing written
        Assert.False(File.Exists(fixture.SettingsPath));
        Assert.True(SettingsStore.WriteDelay <= TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public void ASaveThatFailsKeepsTheSettingInMemoryAndSaysSoAndRetries()
    {
        using var fixture = new SettingsFixture();
        using var store = fixture.Open();
        var failures = 0;
        store.SaveFailed += () => failures++;
        Directory.CreateDirectory(fixture.SettingsPath); // a folder where the file should be: the move fails
        store.Set("general.theme", "Dark");
        Assert.False(store.Flush());
        Assert.True(store.LastSaveFailed);
        Assert.Equal(1, failures);
        Assert.Equal("Dark", store.Value<string>("general.theme")); // still applies this run
        Directory.Delete(fixture.SettingsPath);
        Assert.True(store.Flush()); // retried
        Assert.False(store.LastSaveFailed);
    }

    // ---- SET-19, SET-27, migration -----------------------------------------------------------------------------------------------------

    [Fact]
    public void SET19_ResetAllRestoresDefaultsForUnlockedSettingsAndLeavesLockedOnesAlone()
    {
        using var fixture = new SettingsFixture();
        using var store = fixture.Open();
        store.Set("general.theme", "Dark");
        store.Set("recent.maxItems", 3);
        store.Set("links.behaviour", "Block");
        fixture.SetPolicy(PolicyScope.Machine, SettingsFixture.Dword("ExternalLinks", 0));
        var events = new List<string[]>();
        store.SettingsChanged += keys => events.Add([.. keys]);

        Assert.Equal(2, store.ResetAll());
        Assert.Equal(("System", 20), (store.Value<string>("general.theme"), store.Value<int>("recent.maxItems")));
        Assert.Equal("Block", store.Effective("links.behaviour").Text); // locked: untouched
        Assert.Equal("Block", store.UserValue("links.behaviour")); // and the person's own value is still there under it
        Assert.Single(events);
        Assert.Equal(0, store.ResetAll());
    }

    [Fact]
    public void SET27_TheSettingsFileHoldsNoDocumentPathsOrText()
    {
        using var fixture = new SettingsFixture();
        using (var store = fixture.Open())
        {
            foreach (var setting in SettingsRegistry.All.Where(s => s.Kind == SettingKind.Enum))
            {
                store.Set(setting.Key, setting.Values![^1]);
            }

            store.Set("recent.maxItems", 9);
            store.Set("view.restorePosition", false);
            store.Flush();
        }

        var text = File.ReadAllText(fixture.SettingsPath);
        Assert.DoesNotContain(@":\", text, StringComparison.Ordinal);
        Assert.DoesNotContain(".pdf", text, StringComparison.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(text);
        var known = SettingsRegistry.All.Select(s => s.Key).ToHashSet();
        Assert.All(document.RootElement.GetProperty("values").EnumerateObject(), p => Assert.Contains(p.Name, known));
    }

    [Fact]
    public void ThePageColourMovesFromTheOldStoreOnTheFirstRunAndOnlyThen()
    {
        using var fixture = new SettingsFixture();
        var documentsPath = Path.Combine(fixture.Directory, "documents.json");
        var documents = new DocumentStore(documentsPath);
        documents.AddRecent(@"C:\docs\a.pdf");
        documents.SavePosition(@"C:\docs\a.pdf", 4, 0.25, 1.5, MikePDF.Core.Layout.FitMode.None, 1);
        documents.SetPageColour(PageColourMode.Inverted);

        using (var first = fixture.Open())
        {
            Assert.False(first.FileExisted);
            Assert.True(LegacyMigration.MigratePageColour(documents, first));
            Assert.Equal("Inverted", first.Value<string>("view.pageColour"));
        }

        // History is not settings: it stays where it was, and keeps working.
        var reopened = new DocumentStore(documentsPath);
        Assert.Equal(@"C:\docs\a.pdf", Assert.Single(reopened.Recent).Path);
        Assert.Equal(4, reopened.GetPosition(@"C:\docs\a.pdf")!.Page);

        // The second run has a settings file, so the old value is not applied again over the person's later choice.
        using var second = fixture.Open();
        second.Set("view.pageColour", "Sepia");
        second.Flush();
        using var third = fixture.Open();
        Assert.True(third.FileExisted);
        Assert.False(LegacyMigration.MigratePageColour(documents, third));
        Assert.Equal("Sepia", third.Value<string>("view.pageColour"));
    }

    [Fact]
    public void ANormalOldPageColourMigratesNothing()
    {
        using var fixture = new SettingsFixture();
        var documents = new DocumentStore(Path.Combine(fixture.Directory, "documents.json"));
        using var store = fixture.Open();
        Assert.False(LegacyMigration.MigratePageColour(documents, store));
        Assert.Equal("Normal", store.Value<string>("view.pageColour"));
    }

    [Fact]
    public void TheDocumentStoreHonoursTheRecentLimitAndThePositionSwitch()
    {
        using var fixture = new SettingsFixture();
        var documents = new DocumentStore(Path.Combine(fixture.Directory, "documents.json"));
        for (var i = 0; i < 10; i++)
        {
            documents.AddRecent(@"C:\docs\" + i + ".pdf");
        }

        documents.RecentLimit = 3;
        Assert.Equal(3, documents.Recent.Count);
        Assert.Equal(@"C:\docs\9.pdf", documents.Recent[0].Path);
        documents.RecentLimit = 0;
        Assert.Empty(documents.Recent);
        documents.AddRecent(@"C:\docs\new.pdf");
        Assert.Empty(documents.Recent); // 0 turns the list off
        documents.RecentLimit = 500;
        Assert.Equal(50, documents.RecentLimit);

        documents.SavePosition(@"C:\docs\a.pdf", 2, 0, 1, MikePDF.Core.Layout.FitMode.None, 0);
        Assert.NotNull(documents.GetPosition(@"C:\docs\a.pdf"));
        documents.RestorePositions = false;
        Assert.Null(documents.GetPosition(@"C:\docs\a.pdf"));
        documents.SavePosition(@"C:\docs\b.pdf", 2, 0, 1, MikePDF.Core.Layout.FitMode.None, 0);
        documents.RestorePositions = true;
        Assert.Null(documents.GetPosition(@"C:\docs\b.pdf")); // not recorded while it was off
        documents.ClearPositions();
        Assert.Null(documents.GetPosition(@"C:\docs\a.pdf"));
        Assert.Equal(0, documents.PositionCount);
    }
}
