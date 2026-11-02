using MikePDF.Core.Commands;

namespace MikePDF.Core.Tests;

/// <summary>
/// The command registry and chords (docs/features/command-palette-shortcuts.md). Criterion ids are in the test names: CMD-01 registry integrity, CMD-02
/// default chords, CMD-03 the parser, CMD-04 reserved chords, CMD-08 availability, CMD-17 and CMD-18 and CMD-19 remapping, CMD-22 layouts.
/// </summary>
public sealed class CommandTests
{
    private static readonly CommandContext Doc = new() { HasDocument = true, PageCount = 10, HasPath = true };
    private static readonly string[] ThreeChords = ["Ctrl+Alt+1", "Ctrl+Alt+2", "Ctrl+Alt+3"];

    // ---- CMD-01 ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void CMD01_EveryIdIsUniqueAndWellFormedAndHasANameACategoryAndAScope()
    {
        var ids = CommandCatalog.All.Select(c => c.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        foreach (var command in CommandCatalog.All)
        {
            Assert.Matches(CommandDefinition.IdPattern, command.Id);
            Assert.True(ResourceText.Has(command.NameKey), "No name for " + command.Id + " (" + command.NameKey + ")");
            Assert.False(string.IsNullOrWhiteSpace(ResourceText.Get(command.NameKey)));
            Assert.True(ResourceText.Has(CommandDefinition.CategoryKey(command.Category)), "No category text for " + command.Category);
            Assert.True(Enum.IsDefined(command.Scope));
            if (command.HasKeywords)
            {
                Assert.True(ResourceText.Has(command.KeywordsKey), "No keywords for " + command.Id);
            }
        }
    }

    [Fact]
    public void CMD01_NoIdInTheCommittedSnapshotHasBeenRemovedOrRenamed()
    {
        var snapshot = File.ReadAllLines(Path.Combine(ResourceText.RepoRoot(), "tests", "MikePDF.Core.Tests", "command-ids.txt"))
            .Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        Assert.NotEmpty(snapshot);
        var missing = snapshot.Where(id => CommandCatalog.ById.ContainsKey(id) is false).ToList();
        Assert.True(missing.Count == 0, "Command ids were removed or renamed (users' shortcuts refer to them): " + string.Join(", ", missing));
    }

    [Fact]
    public void CMD01_EveryButtonLabelOverrideAndKeywordsResourceBelongsToARealCommand()
    {
        var stems = CommandCatalog.All.Select(c => "Cmd_" + c.Id.Replace('.', '_') + "_").ToList();
        foreach (var key in ResourceText.All.Keys.Where(k => k.StartsWith("Cmd_", StringComparison.Ordinal)))
        {
            Assert.True(stems.Any(s => key.StartsWith(s, StringComparison.Ordinal)), "Resource " + key + " names no command");
        }
    }

    // ---- CMD-02 ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void CMD02_NoTwoDefaultChordsConflictInOverlappingScopesAndNoDefaultIsReserved()
    {
        var seen = new List<(CommandDefinition Command, Chord Chord)>();
        foreach (var command in CommandCatalog.All)
        {
            Assert.True(command.DefaultChords.Length <= CommandRegistry.MaxChordsPerCommand, command.Id);
            foreach (var chord in command.DefaultChords)
            {
                Assert.False(ReservedChords.IsReserved(chord), command.Id + " defaults to the reserved chord " + chord);
                var clash = seen.FirstOrDefault(s => s.Chord == chord && CommandRegistry.ScopesOverlap(s.Command.Scope, command.Scope));
                Assert.True(clash.Command is null, $"{command.Id} and {clash.Command?.Id} both default to {chord}");
                if (!chord.HasControl && !chord.HasAlt && ChordKeys.IsPrintable(chord.Key))
                {
                    Assert.True(command.Scope is CommandScope.Reader or CommandScope.Presentation, command.Id); // plain printable keys only where nothing is being typed
                }

                seen.Add((command, chord));
            }
        }
    }

    [Fact]
    public void CMD02_ControlAltChordsAreNotDefaultsExceptActualSizeWhichIsChosenForAMeasuredReason()
    {
        var ctrlAlt = CommandCatalog.All.SelectMany(c => c.DefaultChords.Select(x => (c.Id, Chord: x))).Where(p => p.Chord.HasControl && p.Chord.HasAlt).ToList();
        Assert.Equal(["view.zoomPhysical"], ctrlAlt.Select(p => p.Id));
    }

    // ---- CMD-03 ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void CMD03_EveryDefaultRoundTripsThroughTheParserAsCanonicalText()
    {
        foreach (var chord in CommandCatalog.All.SelectMany(c => c.DefaultChords))
        {
            var text = chord.ToString();
            Assert.True(ChordParser.TryParse(text, out var back, out var error), text + ": " + error);
            Assert.Equal(chord, back);
            Assert.Equal(text, back.ToString());
        }
    }

    [Theory]
    [InlineData("ctrl+shift+p", "Ctrl+Shift+P")]
    [InlineData("SHIFT+CTRL+P", "Ctrl+Shift+P")]
    [InlineData("Shift+Alt+Ctrl+F3", "Ctrl+Alt+Shift+F3")]
    [InlineData("Control+Menu+Delete", "Ctrl+Alt+Delete")]
    [InlineData("Ctrl+=", "Ctrl+Plus")]
    [InlineData("Ctrl++", "Ctrl+Plus")]
    [InlineData("Ctrl+-", "Ctrl+Minus")]
    [InlineData("Ctrl+,", "Ctrl+Comma")]
    [InlineData("Alt+Left", "Alt+Left")]
    [InlineData("esc", "Escape")]
    [InlineData("Ctrl+Number0", "Ctrl+0")]
    [InlineData("Ctrl+NumPad0", "Ctrl+NumberPad0")]
    [InlineData("pgdn", "PageDown")]
    [InlineData("+", "Plus")]
    public void CMD03_TheParserAcceptsAnyCaseAnyModifierOrderAndTheAliases(string text, string canonical)
    {
        Assert.True(ChordParser.TryParse(text, out var chord, out var error), error?.ToString());
        Assert.Equal(canonical, chord.ToString());
    }

    [Theory]
    [InlineData("", ChordParseProblem.Empty, 0)]
    [InlineData("   ", ChordParseProblem.Empty, 0)]
    [InlineData("Ctrl+Frobnicate", ChordParseProblem.UnknownKey, 5)]
    [InlineData("Ctrl+A+B", ChordParseProblem.TwoKeys, 7)]
    [InlineData("Ctrl+Ctrl+A", ChordParseProblem.DuplicateModifier, 5)]
    [InlineData("Win+E", ChordParseProblem.WindowsKey, 0)]
    [InlineData("Ctrl+Windows+E", ChordParseProblem.WindowsKey, 5)]
    [InlineData("Ctrl+Shift", ChordParseProblem.NoKey, 10)]
    [InlineData("Ctrl+", ChordParseProblem.NoKey, 5)]
    [InlineData("A+Ctrl", ChordParseProblem.TwoKeys, 2)]
    public void CMD03_BadTextIsAnErrorWithAPositionNotAnException(string text, ChordParseProblem problem, int position)
    {
        Assert.False(ChordParser.TryParse(text, out _, out var error));
        Assert.NotNull(error);
        Assert.Equal(problem, error!.Problem);
        Assert.Equal(position, error.Position);
    }

    [Fact]
    public void CMD03_VeryLongTextIsRefusedNotParsed()
    {
        Assert.False(ChordParser.TryParse(new string('A', 10_000), out _, out var error));
        Assert.NotNull(error);
        Assert.Null(Chord.FromText("not a chord"));
        Assert.Throws<FormatException>(() => ChordParser.Parse("Ctrl+"));
    }

    // ---- CMD-04 ------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Alt+F4")]
    [InlineData("Alt+Tab")]
    [InlineData("Alt+Escape")]
    [InlineData("Alt+Space")]
    [InlineData("Ctrl+Escape")]
    [InlineData("Ctrl+Alt+Delete")]
    [InlineData("PrintScreen")]
    [InlineData("Shift+PrintScreen")]
    [InlineData("F10")]
    [InlineData("Shift+F10")]
    [InlineData("Tab")]
    [InlineData("Shift+Tab")]
    [InlineData("Escape")]
    [InlineData("Ctrl+Shift+0")]
    [InlineData("Ctrl+Shift+Escape")]
    [InlineData("Ctrl+Space")]
    public void CMD04_TheReservedChordsAreReserved(string text)
    {
        Assert.True(ReservedChords.IsReserved(ChordParser.Parse(text)), text);
    }

    [Theory]
    [InlineData("Ctrl+Tab")]
    [InlineData("Ctrl+Shift+Tab")]
    [InlineData("Ctrl+Alt+0")]
    [InlineData("Shift+Escape")]
    [InlineData("F11")]
    [InlineData("Ctrl+F10")]
    [InlineData("Alt+Left")]
    public void CMD04_NearbyChordsAreNotReserved(string text)
    {
        Assert.False(ReservedChords.IsReserved(ChordParser.Parse(text)), text);
    }

    [Fact]
    public void CMD04_BindingAReservedChordIsRefusedAndTheOldShortcutsStay()
    {
        var registry = new CommandRegistry();
        var before = registry.EffectiveChords("view.zoomIn");
        var outcome = registry.SetChords("view.zoomIn", [ChordParser.Parse("Alt+F4")], reassign: true);
        Assert.False(outcome.Applied);
        Assert.Contains(outcome.Problems, p => p.Kind == BindingProblemKind.ReservedChord);
        Assert.Equal(before, registry.EffectiveChords("view.zoomIn"));
    }

    [Fact]
    public void CMD04_APlainLetterCanBeBoundOnlyToACommandThatWorksOnThePage()
    {
        var registry = new CommandRegistry();
        Assert.True(registry.SetChords("view.fitWidth", [ChordParser.Parse("W")], reassign: false).Applied);
        var refused = registry.SetChords("file.open", [ChordParser.Parse("O")], reassign: false);
        Assert.False(refused.Applied);
        Assert.Contains(refused.Problems, p => p.Kind == BindingProblemKind.PlainKeyOutsideReader);
    }

    // ---- CMD-08 ------------------------------------------------------------------------------------------------------------------------

    public static TheoryData<string, string, bool, string?> Availability => new()
    {
        { "view.zoomIn", "none", false, Reasons.NoDocument },
        { "view.zoomIn", "doc", true, null },
        { "find.open", "none", false, Reasons.NoDocument },
        { "find.open", "doc", true, null },
        { "find.close", "doc", false, Reasons.FindNotOpen },
        { "find.close", "find", true, null },
        { "edit.copy", "none", false, Reasons.NoDocument },
        { "edit.copy", "doc", true, null },
        { "edit.copy", "owner", false, Reasons.OwnerNoCopy },
        { "nav.back", "doc", false, Reasons.NoBackHistory },
        { "nav.back", "history", true, null },
        { "nav.forward", "doc", false, Reasons.NoForwardHistory },
        { "nav.forward", "history", true, null },
        { "file.reload", "doc", true, null },
        { "file.reload", "nopath", false, Reasons.NoPath },
        { "file.reload", "busy", false, Reasons.Busy },
        { "file.open", "none", true, null },
        { "file.open", "presentation", false, Reasons.Presentation },
        { "view.zoomIn", "presentation", false, Reasons.Presentation },
        { "view.readingMode", "presentation", true, null },
        { "app.exportSettings", "none", true, null },
        { "app.exportSettings", "policy", false, Reasons.Policy },
        { "app.importSettings", "policy", false, Reasons.Policy },
        { "app.exportShortcuts", "policy", false, Reasons.Policy },
        { "app.importShortcuts", "policy", false, Reasons.Policy },
        { "app.undoImportSettings", "none", false, Reasons.NothingToUndo },
        { "app.undoImportSettings", "canundo", true, null },
        { "app.undoImportSettings", "policy", false, Reasons.Policy },
        { "app.clearRecent", "none", false, Reasons.NoRecent },
        { "app.clearRecent", "recent", true, null },
        { "app.clearUsageHistory", "none", false, Reasons.NothingToClear },
        { "app.resetShortcuts", "none", false, Reasons.NoCustomShortcuts },
        { "app.resetShortcuts", "custom", true, null },
        { "app.commandPalette", "none", true, null },
        { "app.commandPalette", "modal", false, Reasons.Busy },
        { "app.commandPalette", "presentation", false, Reasons.Presentation },
        { "app.settings", "none", true, null },
    };

    [Theory]
    [MemberData(nameof(Availability))]
    public void CMD08_CanExecuteGivesTheRightAnswerAndReasonForEachSituation(string id, string situation, bool enabled, string? reason)
    {
        var context = situation switch
        {
            "none" => CommandContext.Empty,
            "doc" => Doc,
            "find" => Doc with { FindOpen = true },
            "owner" => Doc with { CanCopy = false },
            "history" => Doc with { CanGoBack = true, CanGoForward = true },
            "nopath" => Doc with { HasPath = false },
            "busy" => Doc with { Busy = true },
            "presentation" => Doc with { Presentation = true },
            "policy" => Doc with { ImportExportAllowed = false },
            "canundo" => CommandContext.Empty with { CanUndoImport = true },
            "recent" => CommandContext.Empty with { HasRecent = true },
            "custom" => CommandContext.Empty with { HasCustomShortcuts = true },
            "modal" => Doc with { ModalOpen = true },
            _ => throw new ArgumentOutOfRangeException(nameof(situation)),
        };
        var answer = CommandCatalog.ById[id].CanExecute(context);
        Assert.Equal(enabled, answer.IsEnabled);
        Assert.Equal(reason, answer.Reason);
        if (reason is not null)
        {
            Assert.True(ResourceText.Has(Reasons.ResourceKey(reason)), "No text for the reason " + reason);
        }
    }

    [Fact]
    public void CMD08_EveryReasonAnyCommandCanGiveHasText()
    {
        var contexts = new[]
        {
            CommandContext.Empty, Doc, Doc with { FindOpen = true }, Doc with { CanCopy = false }, Doc with { CanGoBack = true }, Doc with { HasPath = false },
            Doc with { Busy = true }, Doc with { Presentation = true }, Doc with { ImportExportAllowed = false }, Doc with { ModalOpen = true }, Doc with { HasRecent = true },
        };
        foreach (var context in contexts)
        {
            foreach (var command in CommandCatalog.All)
            {
                var answer = command.CanExecute(context);
                Assert.Equal(answer.IsEnabled, answer.Reason is null);
                if (answer.Reason is not null)
                {
                    Assert.True(ResourceText.Has(Reasons.ResourceKey(answer.Reason)), command.Id + ": " + answer.Reason);
                }
            }
        }
    }

    // ---- CMD-17, CMD-18, CMD-19 --------------------------------------------------------------------------------------------------------

    [Fact]
    public void CMD17_ABindingThatClashesOffersReassignAndReassignLeavesExactlyOneOwner()
    {
        var registry = new CommandRegistry();
        var chord = ChordParser.Parse("Ctrl+F"); // find.open
        var refused = registry.SetChords("view.fitWidth", [chord], reassign: false);
        Assert.False(refused.Applied);
        Assert.True(refused.HasConflicts);
        Assert.Equal("find.open", refused.Problems.Single().Other);
        Assert.Equal([ChordParser.Parse("Ctrl+2")], registry.EffectiveChords("view.fitWidth")); // nothing changed

        var done = registry.SetChords("view.fitWidth", [chord], reassign: true);
        Assert.True(done.Applied);
        Assert.Equal([chord], registry.EffectiveChords("view.fitWidth"));
        Assert.Empty(registry.EffectiveChords("find.open"));
        Assert.Equal(["view.fitWidth"], registry.CommandsFor(chord).Select(c => c.Id));
    }

    [Fact]
    public void CMD17_ReassigningOneOfTwoShortcutsLeavesTheOtherWithTheCommand()
    {
        var registry = new CommandRegistry();
        Assert.True(registry.SetChords("view.fitWidth", [ChordParser.Parse("Ctrl+Add")], reassign: true).Applied); // zoomIn has Ctrl+Plus and Ctrl+Add
        Assert.Equal([ChordParser.Parse("Ctrl+Plus")], registry.EffectiveChords("view.zoomIn"));
    }

    [Fact]
    public void CMD17_AChordThatOnlyClashesInANonOverlappingScopeIsNotAConflict()
    {
        var registry = new CommandRegistry([
            new CommandDefinition("a.one", CommandCategory.File, CommandScope.Reader, string.Empty, [ChordParser.Parse("Ctrl+K")], _ => CommandAvailability.Enabled),
            new CommandDefinition("a.two", CommandCategory.File, CommandScope.Editing, string.Empty, [], _ => CommandAvailability.Enabled),
        ]);
        Assert.True(registry.SetChords("a.two", [ChordParser.Parse("Ctrl+K")], reassign: false).Applied);
    }

    [Fact]
    public void CMD17_UnbindingACommandLeavesItInTheRegistryAndRunnableFromThePaletteSource()
    {
        var registry = new CommandRegistry();
        Assert.True(registry.SetChords("view.zoomIn", [], reassign: false).Applied);
        Assert.Empty(registry.EffectiveChords("view.zoomIn"));
        Assert.Contains(registry.Commands, c => c.Id == "view.zoomIn");
        var source = new CommandPaletteSource(registry, () => Doc, ResourceText.Get);
        var row = source.Items(string.Empty).Single(i => i.Key == "view.zoomIn");
        Assert.True(row.Enabled);
        Assert.Equal(string.Empty, row.Shortcut);
    }

    [Fact]
    public void CMD18_AtMostTwoChordsPerCommand()
    {
        var registry = new CommandRegistry();
        var three = ThreeChords.Select(ChordParser.Parse).ToList();
        var outcome = registry.SetChords("view.fitPage", three, reassign: true);
        Assert.False(outcome.Applied);
        Assert.Contains(outcome.Problems, p => p.Kind == BindingProblemKind.TooManyChords);
        Assert.True(registry.SetChords("view.fitPage", three.Take(2).ToList(), reassign: true).Applied);
    }

    [Fact]
    public void CMD19_OnlyOverridesAreStoredAndPuttingTheDefaultBackRemovesTheOverride()
    {
        var registry = new CommandRegistry();
        Assert.Empty(registry.Overrides);
        registry.SetChords("view.fitPage", [ChordParser.Parse("Ctrl+Alt+1")], reassign: false);
        var overrides = registry.Overrides;
        Assert.Equal("view.fitPage", overrides.Single().Command);
        Assert.Equal(["Ctrl+Alt+1"], overrides.Single().Chords);

        registry.SetChords("view.fitPage", [ChordParser.Parse("Ctrl+1")], reassign: false);
        Assert.Empty(registry.Overrides);
        registry.SetChords("view.fitPage", [ChordParser.Parse("Ctrl+Alt+1")], reassign: false);
        registry.Reset("view.fitPage");
        Assert.Empty(registry.Overrides);
        registry.SetChords("view.fitPage", [], reassign: false);
        registry.ResetAll();
        Assert.False(registry.HasCustomShortcuts);
        Assert.Equal([ChordParser.Parse("Ctrl+1")], registry.EffectiveChords("view.fitPage"));
    }

    [Fact]
    public void CMD19_ACommandTheUserNeverTouchedFollowsAChangedDefaultAndAClashKeepsTheUsersChoice()
    {
        CommandDefinition Make(string id, CommandScope scope, params string[] chords) =>
            new(id, CommandCategory.File, scope, string.Empty, chords.Select(ChordParser.Parse), _ => CommandAvailability.Enabled);

        // Version 1 of the catalogue: the user gave "other.cmd" the chord Ctrl+K.
        var v1 = new CommandRegistry([Make("some.cmd", CommandScope.Global, "Ctrl+J"), Make("other.cmd", CommandScope.Global)]);
        v1.SetChords("other.cmd", [ChordParser.Parse("Ctrl+K")], reassign: false);
        var stored = v1.Overrides;

        // Version 2 changes the default of some.cmd to Ctrl+L (follows, since untouched) ...
        var v2 = new CommandRegistry([Make("some.cmd", CommandScope.Global, "Ctrl+L"), Make("other.cmd", CommandScope.Global)]);
        Assert.Empty(v2.LoadOverrides(stored));
        Assert.Equal([ChordParser.Parse("Ctrl+L")], v2.EffectiveChords("some.cmd"));
        Assert.Empty(v2.UpdateConflicts);

        // ... and a version 3 default that clashes with the user's chord loses, and is listed.
        var v3 = new CommandRegistry([Make("some.cmd", CommandScope.Global, "Ctrl+K", "Ctrl+M"), Make("other.cmd", CommandScope.Global)]);
        Assert.Empty(v3.LoadOverrides(stored));
        Assert.Equal([ChordParser.Parse("Ctrl+M")], v3.EffectiveChords("some.cmd"));
        Assert.Equal([ChordParser.Parse("Ctrl+K")], v3.EffectiveChords("other.cmd"));
        var conflict = Assert.Single(v3.UpdateConflicts);
        Assert.Equal(("some.cmd", ChordParser.Parse("Ctrl+K"), "other.cmd"), (conflict.Command, conflict.Chord, conflict.ClaimedBy));
    }

    [Fact]
    public void CMD19_StoredShortcutsThatCannotBeReadAreDroppedAndReportedNotFatal()
    {
        var registry = new CommandRegistry();
        var problems = registry.LoadOverrides([
            new KeyBinding("view.fitPage", ["Ctrl+Alt+1"]),
            new KeyBinding("no.suchCommand", ["Ctrl+Alt+2"]),
            new KeyBinding("view.fitWidth", ["Ctrl+Frobnicate"]),
            new KeyBinding("view.zoomIn", ["Alt+F4"]),
        ]);
        Assert.Equal(
            [BindingProblemKind.UnknownCommand, BindingProblemKind.InvalidChord, BindingProblemKind.ReservedChord],
            problems.Select(p => p.Kind).Order());
        Assert.Equal([ChordParser.Parse("Ctrl+Alt+1")], registry.EffectiveChords("view.fitPage"));
        Assert.Equal([ChordParser.Parse("Ctrl+2")], registry.EffectiveChords("view.fitWidth"));
    }

    [Fact]
    public void CMD17_TheNavigationKeysAreCommandsLikeAnyOtherSoUnbindingOneFreesItsKeyForGood()
    {
        // The page surface no longer has a key handler for them (it was moved into the dispatcher), so a key that no command holds is simply not handled by anything.
        var registry = new CommandRegistry();
        var dispatcher = new ChordDispatcher(registry);
        var right = ChordParser.Parse("Right");
        var onSurface = new DispatchInput { Context = CommandContext.Empty with { HasDocument = true, PageCount = 5 }, SurfaceFocused = true };
        Assert.Equal(DispatchKind.Execute, dispatcher.Dispatch(right, onSurface).Kind);
        Assert.Equal("nav.nextPage", dispatcher.Dispatch(right, onSurface).Command!.Id);
        registry.SetChords("nav.nextPage", [ChordParser.Parse("Ctrl+Alt+N")], reassign: false);
        Assert.Equal(DispatchKind.NotHandled, dispatcher.Dispatch(right, onSurface).Kind);
        registry.Reset("nav.nextPage");
        Assert.Equal(DispatchKind.Execute, dispatcher.Dispatch(right, onSurface).Kind);
    }

    // ---- CMD-22 ------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("en-US", "=", "-", ",")]
    [InlineData("en-GB", "=", "-", ",")]
    [InlineData("de-DE", "+", "-", ",")]
    [InlineData("fr-FR", "=", null, ",")]
    [InlineData("ja-JP", ";", "-", ",")]
    public void CMD22_TheLayoutTableGivesTheKeyNamesAndZoomIsReachableOnEveryLayout(string culture, string plus, string? minus, string comma)
    {
        var layout = ChordDisplay.LayoutFor(culture);
        Assert.NotNull(layout);
        Assert.Equal(plus, layout.CharacterFor(0xBB));
        Assert.Equal(minus, layout.CharacterFor(0xBD));
        Assert.Equal(comma, layout.CharacterFor(0xBC));
        Assert.Equal("Ctrl+" + plus, ChordDisplay.Format(ChordParser.Parse("Ctrl+Plus"), layout));
        Assert.Equal("Ctrl+Num +", ChordDisplay.Format(ChordParser.Parse("Ctrl+Add"), layout));
        Assert.Equal("Ctrl+Num -", ChordDisplay.Format(ChordParser.Parse("Ctrl+Subtract"), layout));
        Assert.Equal("Ctrl+Num 0", ChordDisplay.Format(ChordParser.Parse("Ctrl+NumberPad0"), layout));

        foreach (var id in new[] { "view.zoomIn", "view.zoomOut" })
        {
            Assert.Contains(CommandCatalog.ById[id].DefaultChords, chord => layout.CanPress(chord));
        }
    }

    [Fact]
    public void CMD22_ChordsShowInPeoplesKeyNames()
    {
        Assert.Equal("Ctrl+Shift+P", ChordDisplay.Format(ChordParser.Parse("ctrl+shift+p")));
        Assert.Equal("Page Down", ChordDisplay.Format(ChordParser.Parse("PageDown")));
        Assert.Equal("Alt+Left", ChordDisplay.Format(ChordParser.Parse("Alt+Left")));
        Assert.Equal("Ctrl+=, Ctrl+Num +", ChordDisplay.Format(CommandCatalog.ById["view.zoomIn"].DefaultChords, KeyLayout.EnUs));
        Assert.Equal("Ctrl+Esc", ChordDisplay.Format(ChordParser.Parse("Ctrl+Escape")));
        Assert.Equal("Ctrl+;", ChordDisplay.Format(ChordParser.Parse("Ctrl+Plus"), KeyLayout.JaJp));
        Assert.Equal("Ctrl+Q", ChordDisplay.Format(ChordParser.Parse("Ctrl+Plus"), KeyLayout.EnUs, key => key == 0xBB ? "Q" : null)); // the platform's answer wins over the table
    }
}
