using MikePDF.Core.Settings;

namespace MikePDF.Core.Tests;

/// <summary>The shell settings and policies (docs/features/shell-extensions.md 3.5): defaults, the person's choice, locking by policy, and a read that never writes (SHELL-15, SHELL-16).</summary>
public sealed class ShellSwitchesTests : IDisposable
{
    private readonly SettingsFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private void WriteSettings(string json) => File.WriteAllText(_fixture.SettingsPath, json);

    private ShellSwitches Read() => ShellSwitches.Read(_fixture.SettingsPath, _fixture.Provider);

    [Fact]
    public void ThumbnailsAndPreviewAreOnByDefaultWithThreeEngines()
    {
        Assert.Equal(new ShellSwitches(true, true, 3), Read());
        Assert.Equal(ShellSwitches.Defaults, Read());
        Assert.True((bool)SettingsRegistry.Find("shell.thumbnails")!.Default);
        Assert.True((bool)SettingsRegistry.Find("shell.preview")!.Default);
        Assert.Equal(3, SettingsRegistry.Find("shell.maxEngines")!.Default);
    }

    [Fact]
    public void ThePersonCanTurnEachOneOffAndSetTheEngineCount()
    {
        WriteSettings("""{ "schemaVersion": 1, "values": { "shell.thumbnails": false, "shell.maxEngines": 2 } }""");
        Assert.Equal(new ShellSwitches(false, true, 2), Read());
        WriteSettings("""{ "schemaVersion": 1, "values": { "shell.preview": false } }""");
        Assert.Equal(new ShellSwitches(true, false, 3), Read());
    }

    [Fact]
    public void AValueOutOfRangeOrOfTheWrongTypeFallsBackToTheDefaultForThatKeyOnly()
    {
        WriteSettings("""{ "values": { "shell.maxEngines": 99, "shell.thumbnails": "no", "shell.preview": false } }""");
        Assert.Equal(new ShellSwitches(true, false, 3), Read());
    }

    [Fact]
    public void AMissingOrDamagedFileMeansDefaultsAndIsNeverSetAside()
    {
        Assert.Equal(ShellSwitches.Defaults, Read());
        foreach (var damaged in new[] { "", "{ not json", "[1,2,3]", "\"text\"" })
        {
            WriteSettings(damaged);
            Assert.Equal(ShellSwitches.Defaults, Read());
            Assert.True(File.Exists(_fixture.SettingsPath), "ShellHost only reads: a damaged file is the app's to set aside");
            Assert.Equal(damaged, File.ReadAllText(_fixture.SettingsPath));
            Assert.Empty(Directory.GetFiles(_fixture.Directory, "settings.corrupt-*.json"));
        }
    }

    [Fact]
    public void AnOversizeFileIsIgnoredWithoutBeingParsed()
    {
        WriteSettings("""{ "values": { "shell.thumbnails": false } }""" + new string(' ', (int)SettingsStore.MaxFileBytes));
        Assert.Equal(ShellSwitches.Defaults, Read());
    }

    [Fact]
    public void ThePolicyOffLocksTheSettingOffWhateverThePersonChose()
    {
        WriteSettings("""{ "values": { "shell.thumbnails": true, "shell.preview": true } }""");
        _fixture.SetPolicy(PolicyScope.Machine, SettingsFixture.Dword("ShellThumbnailsEnabled", 0));
        Assert.Equal(new ShellSwitches(false, true, 3), Read());
        _fixture.SetPolicy(PolicyScope.Machine, SettingsFixture.Dword("ShellPreviewEnabled", 0));
        Assert.Equal(new ShellSwitches(false, false, 3), Read());
    }

    [Fact]
    public void ThePolicyOnLeavesThePersonsChoiceAlone()
    {
        WriteSettings("""{ "values": { "shell.thumbnails": false } }""");
        _fixture.SetPolicy(PolicyScope.Machine, SettingsFixture.Dword("ShellThumbnailsEnabled", 1));
        Assert.False(Read().ThumbnailsEnabled);
    }

    [Fact]
    public void AnInvalidPolicyValueFailsClosed()
    {
        _fixture.SetPolicy(PolicyScope.Machine, SettingsFixture.Dword("ShellPreviewEnabled", 7));
        Assert.False(Read().PreviewEnabled);
        Assert.True(Read().ThumbnailsEnabled);
    }

    [Fact]
    public void AUserHivePolicyCanOnlyTighten()
    {
        _fixture.SetPolicy(PolicyScope.User, SettingsFixture.Dword("ShellThumbnailsEnabled", 0));
        Assert.False(Read().ThumbnailsEnabled);

        _fixture.SetPolicy(PolicyScope.Machine, SettingsFixture.Dword("ShellPreviewEnabled", 0));
        _fixture.SetPolicy(PolicyScope.User, SettingsFixture.Dword("ShellPreviewEnabled", 1));
        Assert.False(Read().PreviewEnabled);
    }

    [Fact]
    public void TheEngineCountFollowsThePolicyAndCannotBeRaisedByAUser()
    {
        WriteSettings("""{ "values": { "shell.maxEngines": 5 } }""");
        Assert.Equal(5, Read().MaxEngines);
        _fixture.SetPolicy(PolicyScope.Machine, SettingsFixture.Dword("ShellMaxEngines", 2));
        Assert.Equal(2, Read().MaxEngines);
        _fixture.SetPolicy(PolicyScope.User, SettingsFixture.Dword("ShellMaxEngines", 6));
        Assert.Equal(2, Read().MaxEngines);
    }

    [Fact]
    public void TheAppsSettingsStoreAndShellHostAgree()
    {
        WriteSettings("""{ "values": { "shell.thumbnails": false, "shell.maxEngines": 4 } }""");
        _fixture.SetPolicy(PolicyScope.Machine, SettingsFixture.Dword("ShellPreviewEnabled", 0));
        using var store = _fixture.Open();
        var read = Read();
        Assert.Equal(store.Value<bool>("shell.thumbnails"), read.ThumbnailsEnabled);
        Assert.Equal(store.Value<bool>("shell.preview"), read.PreviewEnabled);
        Assert.Equal(store.Value<int>("shell.maxEngines"), read.MaxEngines);
        Assert.True(store.Effective("shell.preview").Locked);
    }

    [Fact]
    public void TheShellSettingsAppearInPrivacyAndHaveText()
    {
        foreach (var key in new[] { "shell.thumbnails", "shell.preview" })
        {
            var definition = SettingsRegistry.Find(key)!;
            Assert.Equal(SettingSection.Privacy, definition.Section);
            Assert.True(ResourceText.Has(definition.NameKey), definition.NameKey);
            Assert.True(ResourceText.Has(definition.DescriptionKey), definition.DescriptionKey);
        }

        // Until the integration workflow records that the shell falls through to the next handler, the words say what a declined request shows.
        Assert.Contains("generic PDF icon", ResourceText.Get(SettingsRegistry.Find("shell.thumbnails")!.DescriptionKey), StringComparison.Ordinal);
        Assert.Contains("doesn't delete thumbnails Windows already saved", ResourceText.Get(SettingsRegistry.Find("shell.thumbnails")!.DescriptionKey), StringComparison.Ordinal);
    }
}
