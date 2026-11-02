using System.Text;
using System.Text.Json;
using MikePDF.Core.Commands;
using MikePDF.Core.Settings;

namespace MikePDF.Core.Tests;

/// <summary>Export and import of settings and shortcuts: SET-14 to SET-18, CMD-20, CMD-21 and CMD-28, and the link between the registry and the settings file.</summary>
public sealed class TransferTests
{
    private static byte[] Json(string text) => Encoding.UTF8.GetBytes(text);

    // ---- SET-14 ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void SET14_TheExportHoldsOnlyExportableUserValuesAndShortcutsAndNotLockedValues()
    {
        using var fixture = new SettingsFixture();
        using var store = fixture.Open();
        var registry = new CommandRegistry();
        using var sync = new KeybindingSync(registry, store);
        store.Set("general.theme", "Dark");
        store.Set("recent.maxItems", 7);
        store.Set("links.behaviour", "Block");
        registry.SetChords("view.fitPage", [ChordParser.Parse("Ctrl+Alt+1")], reassign: false);
        fixture.SetPolicy(PolicyScope.Machine, SettingsFixture.Dword("ExternalLinks", 0)); // links.behaviour is now locked

        using var document = JsonDocument.Parse(SettingsTransfer.ExportSettings(store));
        var root = document.RootElement;
        Assert.Equal("mikepdf-settings", root.GetProperty("format").GetString());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        var values = root.GetProperty("values").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ToString());
        Assert.Equal(new Dictionary<string, string> { ["general.theme"] = "Dark", ["recent.maxItems"] = "7" }, values);
        var binding = Assert.Single(root.GetProperty("keybindings").EnumerateArray());
        Assert.Equal("view.fitPage", binding.GetProperty("command").GetString());
        Assert.Equal(["Ctrl+Alt+1"], binding.GetProperty("chords").EnumerateArray().Select(c => c.GetString()));
        Assert.DoesNotContain(@":\", root.GetRawText(), StringComparison.Ordinal);
        Assert.True(ResourceText.Has("Settings_Export_AuthorNote")); // the dialog says the author name goes with it
    }

    // ---- SET-15, SET-16 ----------------------------------------------------------------------------------------------------------------

    private static byte[] CraftedFile() => Json("""
        { "format": "mikepdf-settings", "schemaVersion": 1,
          "values": {
            "general.theme": "Dark", "recent.maxItems": 30, "view.restorePosition": false, "view.pageColour": "Sepia",
            "links.behaviour": "Confirm",
            "future.setting": 1, "other.unknown": "x",
            "engines.maxLiveTabs": 500, "save.mode": "Neon", "markup.authorName": 12 },
          "keybindings": [ { "command": "view.fitPage", "chords": ["Ctrl+Alt+1"] }, { "command": "nope.nope", "chords": ["Ctrl+Alt+2"] } ] }
        """);

    [Fact]
    public void SET15_ThePreviewCountsWhatWouldChangeWhatIsLockedWhatIsUnknownAndWhatIsInvalidAndChangesNothing()
    {
        using var fixture = new SettingsFixture();
        using var store = fixture.Open();
        var registry = new CommandRegistry();
        store.Set("view.pageColour", "Sepia"); // already what the file says: not a change
        fixture.SetPolicy(PolicyScope.Machine, SettingsFixture.Dword("ExternalLinks", 0));

        var preview = SettingsTransfer.Preview(CraftedFile(), store, registry);
        Assert.True(preview.IsAccepted);
        Assert.Equal(["general.theme", "recent.maxItems", "view.restorePosition"], preview.Changes.Select(c => c.Key));
        Assert.Equal(["links.behaviour"], preview.Locked);
        Assert.Equal(["future.setting", "other.unknown"], preview.Unknown);
        Assert.Equal(["engines.maxLiveTabs", "save.mode", "markup.authorName"], preview.Invalid);
        Assert.Equal(["view.fitPage"], preview.Keybindings!.Select(b => b.Command));
        Assert.Equal([BindingProblemKind.UnknownCommand], preview.BindingProblems.Select(p => p.Kind));

        // Nothing has changed yet.
        Assert.Equal(("System", 20, true), (store.Value<string>("general.theme"), store.Value<int>("recent.maxItems"), store.Value<bool>("view.restorePosition")));
        Assert.False(registry.HasCustomShortcuts);
        Assert.False(File.Exists(SettingsTransfer.PreviousFile(store)));
    }

    [Fact]
    public void SET15_ConfirmingAppliesEverythingAndCancellingAppliesNothing()
    {
        using var fixture = new SettingsFixture();
        using var store = fixture.Open();
        var registry = new CommandRegistry();
        using var sync = new KeybindingSync(registry, store);
        store.Set("links.behaviour", "Block");
        var preview = SettingsTransfer.Preview(CraftedFile(), store, registry);

        // Cancel: the preview is simply dropped.
        Assert.Equal("System", store.Value<string>("general.theme"));

        SettingsTransfer.Apply(preview, store);
        Assert.Equal(("Dark", 30, false, "Sepia"), (store.Value<string>("general.theme"), store.Value<int>("recent.maxItems"), store.Value<bool>("view.restorePosition"), store.Value<string>("view.pageColour")));
        Assert.Equal("Confirm", store.Value<string>("links.behaviour")); // the file's value, not the Block that was set before
        Assert.Equal([ChordParser.Parse("Ctrl+Alt+1")], registry.EffectiveChords("view.fitPage"));
        Assert.True(SettingsTransfer.CanUndo(store));
    }

    [Fact]
    public void SET15_AValueTheFileSetsToWhatItAlreadyIsIsNotCountedAsAChange()
    {
        using var fixture = new SettingsFixture();
        using var store = fixture.Open();
        var registry = new CommandRegistry();
        var file = Json("""{ "format": "mikepdf-settings", "schemaVersion": 1, "values": { "general.theme": "System", "recent.maxItems": 20 } }""");
        var preview = SettingsTransfer.Preview(file, store, registry);
        Assert.True(preview.IsAccepted);
        Assert.Empty(preview.Changes);
        Assert.Null(preview.Keybindings); // no shortcut list in the file: shortcuts are left alone
    }

    public static TheoryData<string, ImportRejection> Rejected => new()
    {
        { "not json", ImportRejection.NotJson },
        { "[1,2]", ImportRejection.NotJson },
        { "{\"format\": \"mikepdf-settings\", \"values\": ", ImportRejection.NotJson },
        { "{\"format\": \"something-else\", \"schemaVersion\": 1}", ImportRejection.WrongFormat },
        { "{\"schemaVersion\": 1, \"values\": {}}", ImportRejection.WrongFormat },
        { "{\"format\": \"mikepdf-shortcuts\", \"schemaVersion\": 1, \"keybindings\": []}", ImportRejection.WrongFormat },
        { "{\"format\": 5}", ImportRejection.WrongFormat },
        { "{\"format\": \"mikepdf-settings\", \"x\": [[[[[[[[[[[1]]]]]]]]]]]}", ImportRejection.NotJson },
        { "{\"format\": \"mikepdf-settings\", // comment\n \"values\": {}}", ImportRejection.NotJson },
    };

    [Theory]
    [MemberData(nameof(Rejected))]
    public void SET16_AFileThatIsNotASettingsFileIsRejectedAndChangesNothing(string content, ImportRejection expected)
    {
        using var fixture = new SettingsFixture();
        using var store = fixture.Open();
        var preview = SettingsTransfer.Preview(Json(content), store, new CommandRegistry());
        Assert.False(preview.IsAccepted);
        Assert.Equal(expected, preview.Rejection);
        SettingsTransfer.Apply(preview, store); // applying a rejected preview does nothing
        Assert.False(File.Exists(SettingsTransfer.PreviousFile(store)));
    }

    [Fact]
    public void SET16_AFileOverTwoHundredFiftySixKibIsRefusedWithoutBeingRead()
    {
        using var fixture = new SettingsFixture();
        using var store = fixture.Open();
        var big = new byte[(256 * 1024) + 1];
        Assert.Equal(ImportRejection.TooLarge, SettingsTransfer.Preview(big, store, new CommandRegistry()).Rejection);
        Assert.Equal(ImportRejection.TooLarge, SettingsTransfer.PreviewShortcuts(big, store, new CommandRegistry()).Rejection);
        using var stream = new LyingStream(4L * 1024 * 1024 * 1024);
        Assert.Null(SettingsTransfer.ReadLimited(stream, out var rejection)); // a "4 GB" file: refused on its length alone
        Assert.Equal(ImportRejection.TooLarge, rejection);
        Assert.Equal(0, stream.BytesRead);
    }

    [Fact]
    public void SET16_ANewerVersionImportsOnlyWhatThisVersionKnowsAfterConfirmation()
    {
        using var fixture = new SettingsFixture();
        using var store = fixture.Open();
        var file = Json("""{ "format": "mikepdf-settings", "schemaVersion": 7, "values": { "general.theme": "Light", "from.the.future": true } }""");
        var preview = SettingsTransfer.Preview(file, store, new CommandRegistry());
        Assert.True(preview.IsAccepted);
        Assert.True(preview.NewerVersion);
        Assert.Equal(["general.theme"], preview.Changes.Select(c => c.Key));
        Assert.Equal(["from.the.future"], preview.Unknown);
    }

    [Fact]
    public void SET16_ValuesOutOfRangeAreReportedAndSkippedNeverClamped()
    {
        using var fixture = new SettingsFixture();
        using var store = fixture.Open();
        var file = Json("""{ "format": "mikepdf-settings", "schemaVersion": 1, "values": { "recent.maxItems": 51, "general.theme": "dark", "view.restorePosition": 1 } }""");
        var preview = SettingsTransfer.Preview(file, store, new CommandRegistry());
        Assert.Empty(preview.Changes);
        Assert.Equal(["recent.maxItems", "general.theme", "view.restorePosition"], preview.Invalid);
        SettingsTransfer.Apply(preview, store);
        Assert.Equal(20, store.Value<int>("recent.maxItems"));
    }

    // ---- SET-17 ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void SET17_UndoImportPutsTheOldFileBackExactlyAndIsGoneAfterwards()
    {
        using var fixture = new SettingsFixture();
        using var store = fixture.Open();
        var registry = new CommandRegistry();
        using var sync = new KeybindingSync(registry, store);
        store.Set("general.theme", "Light");
        registry.SetChords("view.fitWidth", [ChordParser.Parse("Ctrl+Alt+2")], reassign: false);
        store.Flush();
        var before = File.ReadAllBytes(fixture.SettingsPath);

        SettingsTransfer.Apply(SettingsTransfer.Preview(CraftedFile(), store, registry), store);
        store.Flush();
        Assert.NotEqual(before, File.ReadAllBytes(fixture.SettingsPath));
        Assert.Equal(before, File.ReadAllBytes(SettingsTransfer.PreviousFile(store)));
        Assert.Equal("Dark", store.Value<string>("general.theme"));

        Assert.True(SettingsTransfer.Undo(store));
        Assert.Equal(before, File.ReadAllBytes(fixture.SettingsPath)); // byte for byte
        Assert.Equal("Light", store.Value<string>("general.theme"));
        Assert.Equal([ChordParser.Parse("Ctrl+Alt+2")], registry.EffectiveChords("view.fitWidth"));
        Assert.Equal(20, store.Value<int>("recent.maxItems"));
        Assert.False(SettingsTransfer.CanUndo(store));
        Assert.False(SettingsTransfer.Undo(store));
    }

    [Fact]
    public void SET17_TheSecondImportReplacesTheFileThatUndoWouldRestore()
    {
        using var fixture = new SettingsFixture();
        using var store = fixture.Open();
        var registry = new CommandRegistry();
        SettingsTransfer.Apply(SettingsTransfer.Preview(Json("""{ "format": "mikepdf-settings", "schemaVersion": 1, "values": { "general.theme": "Dark" } }"""), store, registry), store);
        SettingsTransfer.Apply(SettingsTransfer.Preview(Json("""{ "format": "mikepdf-settings", "schemaVersion": 1, "values": { "general.theme": "Light" } }"""), store, registry), store);
        Assert.True(SettingsTransfer.Undo(store));
        Assert.Equal("Dark", store.Value<string>("general.theme")); // back to after the first, not before it
        Assert.False(SettingsTransfer.CanUndo(store));
    }

    [Fact]
    public void SET17_AnImportOnAFreshInstallCanBeUndoneToDefaults()
    {
        using var fixture = new SettingsFixture();
        using var store = fixture.Open();
        Assert.False(File.Exists(fixture.SettingsPath));
        SettingsTransfer.Apply(SettingsTransfer.Preview(Json("""{ "format": "mikepdf-settings", "schemaVersion": 1, "values": { "general.theme": "Dark" } }"""), store, new CommandRegistry()), store);
        Assert.True(SettingsTransfer.Undo(store));
        Assert.Equal("System", store.Value<string>("general.theme"));
    }

    // ---- SET-18 and CMD-21: policy turns import and export off -------------------------------------------------------------------------------

    [Fact]
    public void SET18_WithImportExportTurnedOffByPolicyNothingImportsAndTheCommandsAreUnavailableWithTheReason()
    {
        using var fixture = new SettingsFixture();
        using var store = fixture.Open();
        var registry = new CommandRegistry();
        fixture.SetPolicy(PolicyScope.Machine, SettingsFixture.Dword("SettingsImportExportEnabled", 0));
        Assert.False(store.Policy.Current.ImportExportAllowed);
        Assert.Equal(ImportRejection.PolicyBlocked, SettingsTransfer.Preview(CraftedFile(), store, registry).Rejection);
        Assert.Equal(ImportRejection.PolicyBlocked, SettingsTransfer.PreviewShortcuts(Json("{}"), store, registry).Rejection);

        var context = new CommandContext { ImportExportAllowed = store.Policy.Current.ImportExportAllowed, CanUndoImport = true };
        foreach (var id in new[] { "app.exportSettings", "app.importSettings", "app.exportShortcuts", "app.importShortcuts", "app.undoImportSettings" })
        {
            var answer = CommandCatalog.ById[id].CanExecute(context);
            Assert.False(answer.IsEnabled, id);
            Assert.Equal(Reasons.Policy, answer.Reason);
        }

        var source = new CommandPaletteSource(registry, () => context, ResourceText.Get);
        Assert.All(source.Items(string.Empty).Where(i => i.Key is "app.exportSettings" or "app.importShortcuts"), i => Assert.Equal("Turned off by your organisation", i.DisabledReason));
    }

    [Fact]
    public void SET18_UndoIsAlsoRefusedWhilePolicyHasImportOff()
    {
        using var fixture = new SettingsFixture();
        using var store = fixture.Open();
        SettingsTransfer.Apply(SettingsTransfer.Preview(Json("""{ "format": "mikepdf-settings", "schemaVersion": 1, "values": { "general.theme": "Dark" } }"""), store, new CommandRegistry()), store);
        fixture.SetPolicy(PolicyScope.Machine, SettingsFixture.Dword("SettingsImportExportEnabled", 0));
        Assert.False(SettingsTransfer.Undo(store));
        Assert.Equal("Dark", store.Value<string>("general.theme"));
    }

    // ---- CMD-20 ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void CMD20_ShortcutsExportAndImportRoundTripToTheSameBindings()
    {
        using var fixture = new SettingsFixture();
        using var store = fixture.Open();
        var registry = new CommandRegistry();
        registry.SetChords("view.fitPage", [ChordParser.Parse("Ctrl+Alt+1")], reassign: false);
        registry.SetChords("view.fitWidth", [ChordParser.Parse("Ctrl+Alt+2"), ChordParser.Parse("Ctrl+Alt+3")], reassign: false);
        registry.SetChords("find.next", [], reassign: false); // unbound
        var exported = SettingsTransfer.ExportShortcuts(registry);

        using (var document = JsonDocument.Parse(exported))
        {
            Assert.Equal("mikepdf-shortcuts", document.RootElement.GetProperty("format").GetString());
            Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        }

        var fresh = new CommandRegistry();
        var preview = SettingsTransfer.PreviewShortcuts(exported, store, fresh);
        Assert.True(preview.IsAccepted);
        Assert.Empty(preview.BindingProblems);
        Assert.Equal(3, preview.Keybindings!.Count);
        Assert.Empty(fresh.LoadOverrides(preview.Keybindings!));
        Assert.Equal(registry.Overrides.Select(b => (b.Command, string.Join(",", b.Chords))), fresh.Overrides.Select(b => (b.Command, string.Join(",", b.Chords))));
        Assert.Empty(fresh.EffectiveChords("find.next"));
    }

    [Fact]
    public void CMD20_UnknownInvalidReservedAndConflictingEntriesAreSkippedAndReported()
    {
        using var fixture = new SettingsFixture();
        using var store = fixture.Open();
        var registry = new CommandRegistry();
        var file = Json("""
            { "format": "mikepdf-shortcuts", "schemaVersion": 1, "keybindings": [
              { "command": "view.fitPage", "chords": ["Ctrl+Alt+1"] },
              { "command": "no.suchCommand", "chords": ["Ctrl+Alt+2"] },
              { "command": "view.fitWidth", "chords": ["Ctrl+Frobnicate"] },
              { "command": "view.zoomOut", "chords": ["Alt+F4"] },
              { "command": "view.rotateLeft", "chords": ["Ctrl+F"] },
              { "command": "view.rotateRight", "chords": ["Ctrl+Alt+1"] },
              { "command": "file.open", "chords": ["Ctrl+Alt+4", "Ctrl+Alt+5", "Ctrl+Alt+6"] },
              { "command": "view.fitPage", "chords": ["Ctrl+Alt+7"] } ] }
            """);
        var preview = SettingsTransfer.PreviewShortcuts(file, store, registry);
        Assert.True(preview.IsAccepted);
        Assert.Equal(["view.fitPage"], preview.Keybindings!.Select(b => b.Command));
        Assert.Equal(
            [
                BindingProblemKind.UnknownCommand,
                BindingProblemKind.InvalidChord,
                BindingProblemKind.ReservedChord,
                BindingProblemKind.Conflict, // Ctrl+F is find.open's, which the file does not touch
                BindingProblemKind.Conflict, // Ctrl+Alt+1 is already taken by the first entry
                BindingProblemKind.TooManyChords,
                BindingProblemKind.DuplicateCommand,
            ],
            preview.BindingProblems.Select(p => p.Kind));
        Assert.Equal("find.open", preview.BindingProblems.First(p => p.Kind == BindingProblemKind.Conflict).Other);
        Assert.False(registry.HasCustomShortcuts); // nothing applied
    }

    [Fact]
    public void CMD20_MoreThanAThousandEntriesAndTheWrongFormatAreRejected()
    {
        using var fixture = new SettingsFixture();
        using var store = fixture.Open();
        var entries = string.Join(",", Enumerable.Range(0, 1001).Select(_ => "{\"command\":\"a.b\",\"chords\":[]}"));
        var preview = SettingsTransfer.PreviewShortcuts(Json("{\"format\":\"mikepdf-shortcuts\",\"schemaVersion\":1,\"keybindings\":[" + entries + "]}"), store, new CommandRegistry());
        Assert.Equal(ImportRejection.TooManyEntries, preview.Rejection);
        Assert.Equal(ImportRejection.WrongFormat, SettingsTransfer.PreviewShortcuts(CraftedFile(), store, new CommandRegistry()).Rejection);
        Assert.Equal(ImportRejection.WrongFormat, SettingsTransfer.PreviewShortcuts(Json("{\"format\":\"mikepdf-shortcuts\",\"schemaVersion\":1}"), store, new CommandRegistry()).Rejection);
    }

    [Fact]
    public void CMD28_AHostileShortcutFileIsRejectedBeforeParsingWithoutAllocatingMoreThanOneMebibyte()
    {
        using var fixture = new SettingsFixture();
        using var store = fixture.Open();

        // A file that claims to be 4 GB: refused from its length, not one byte read.
        using var huge = new LyingStream(4L * 1024 * 1024 * 1024);
        Assert.Null(SettingsTransfer.ReadLimited(huge, out var rejection));
        Assert.Equal(ImportRejection.TooLarge, rejection);
        Assert.Equal(0, huge.BytesRead);

        // A stream that does not say how long it is, and does not stop: cut off at the limit.
        using var endless = new LyingStream(long.MaxValue, seekable: false);
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Null(SettingsTransfer.ReadLimited(endless, out rejection));
        Assert.Equal(ImportRejection.TooLarge, rejection);
        Assert.True(endless.BytesRead <= (256 * 1024) + (16 * 1024));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1024 * 1024, "more than 1 MiB was allocated refusing an endless stream");

        // Ten million entries is far over the size limit.
        using var tenMillion = new LyingStream(10_000_000L * 30);
        Assert.Null(SettingsTransfer.ReadLimited(tenMillion, out rejection));
        Assert.Equal(ImportRejection.TooLarge, rejection);

        // A small file nested thousands deep: the parser stops at depth 8.
        var small = Json("{\"format\":\"mikepdf-shortcuts\",\"schemaVersion\":1,\"keybindings\":" + new string('[', 5000) + new string(']', 5000) + "}");
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var preview = SettingsTransfer.PreviewShortcuts(small, store, new CommandRegistry());
        Assert.Equal(ImportRejection.NotJson, preview.Rejection);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocatedBefore < 1024 * 1024, "the depth bomb allocated more than 1 MiB");
    }

    // ---- the link between the registry and the settings file --------------------------------------------------------------------------------

    [Fact]
    public void ARemapIsStoredAndComesBackOnTheNextRun()
    {
        using var fixture = new SettingsFixture();
        using (var store = fixture.Open())
        {
            var registry = new CommandRegistry();
            using var sync = new KeybindingSync(registry, store);
            registry.SetChords("view.zoomIn", [ChordParser.Parse("Ctrl+Alt+Z")], reassign: false);
            store.Flush();
        }

        using var again = fixture.Open();
        var next = new CommandRegistry();
        using var sync2 = new KeybindingSync(next, again);
        Assert.Empty(sync2.Problems);
        Assert.Equal([ChordParser.Parse("Ctrl+Alt+Z")], next.EffectiveChords("view.zoomIn"));
        next.Reset("view.zoomIn");
        Assert.Empty(again.Keybindings); // the override is gone from the file too
    }

    [Fact]
    public void StoredShortcutsThatCannotBeReadAreReportedAtStartup()
    {
        using var fixture = new SettingsFixture();
        File.WriteAllText(fixture.SettingsPath, """{ "schemaVersion": 1, "values": {}, "keybindings": [ { "command": "view.zoomIn", "chords": ["Ctrl+Nonsense"] }, { "command": "gone.command", "chords": ["Ctrl+Alt+Q"] } ] }""");
        using var store = fixture.Open();
        var registry = new CommandRegistry();
        using var sync = new KeybindingSync(registry, store);
        Assert.Equal([BindingProblemKind.InvalidChord, BindingProblemKind.UnknownCommand], sync.Problems.Select(p => p.Kind));
        Assert.Equal([ChordParser.Parse("Ctrl+Plus"), ChordParser.Parse("Ctrl+Add")], registry.EffectiveChords("view.zoomIn"));
    }

    /// <summary>A stream that says it is very long (or does not say) and hands out zeros, to check that a hostile file is refused on its size.</summary>
    private sealed class LyingStream(long length, bool seekable = true) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => seekable;

        public override bool CanWrite => false;

        public override long Length => seekable ? length : throw new NotSupportedException();

        public override long Position
        {
            get => BytesRead;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, length - BytesRead);
            BytesRead += n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
