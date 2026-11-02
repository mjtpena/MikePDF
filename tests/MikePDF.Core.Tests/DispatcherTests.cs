using MikePDF.Core.Commands;

namespace MikePDF.Core.Tests;

/// <summary>The chord dispatcher with fake focus (CMD-05, CMD-06, CMD-07, CMD-26 unit part, CMD-29 unit part).</summary>
public sealed class DispatcherTests
{
    private static readonly CommandContext Doc = new() { HasDocument = true, PageCount = 10 };

    private static (ChordDispatcher Dispatcher, CommandRegistry Registry) Make()
    {
        var registry = new CommandRegistry();
        return (new ChordDispatcher(registry), registry);
    }

    private static DispatchResult Press(ChordDispatcher dispatcher, string chord, DispatchInput? input = null) =>
        dispatcher.Dispatch(ChordParser.Parse(chord), input ?? new DispatchInput { Context = Doc, SurfaceFocused = true });

    [Fact]
    public void CMD05_AReaderChordRunsWhenThePageSurfaceHasFocus()
    {
        var (dispatcher, _) = Make();
        var result = Press(dispatcher, "Ctrl+Plus");
        Assert.Equal(DispatchKind.Execute, result.Kind);
        Assert.Equal("view.zoomIn", result.Command!.Id);
    }

    [Fact]
    public void CMD05_AReaderChordDoesNotRunWhileATextBoxHasFocus()
    {
        var (dispatcher, _) = Make();
        var typing = new DispatchInput { Context = Doc, TextInputFocused = true };
        Assert.Equal(DispatchKind.NotHandled, Press(dispatcher, "Ctrl+Plus", typing).Kind);
        Assert.Equal(DispatchKind.NotHandled, Press(dispatcher, "Ctrl+0", typing).Kind);
    }

    [Theory]
    [InlineData("Ctrl+C")]
    [InlineData("Ctrl+A")]
    [InlineData("Ctrl+Insert")]
    [InlineData("Home")]
    [InlineData("Right")]
    [InlineData("Space")]
    public void CMD05_TextEditingChordsGoToTheTextBoxNotToTheCommand(string chord)
    {
        var (dispatcher, _) = Make();
        Assert.Equal(DispatchKind.NotHandled, Press(dispatcher, chord, new DispatchInput { Context = Doc, TextInputFocused = true }).Kind);
    }

    [Fact]
    public void CMD05_AGlobalChordStillRunsFromATextBox()
    {
        var (dispatcher, _) = Make();
        var typing = new DispatchInput { Context = Doc, TextInputFocused = true };
        Assert.Equal("find.open", Press(dispatcher, "Ctrl+F", typing).Command!.Id);
        Assert.Equal("find.next", Press(dispatcher, "F3", typing).Command!.Id);
        Assert.Equal("nav.back", Press(dispatcher, "Alt+Left", typing).Command!.Id);
        Assert.Equal("app.commandPalette", Press(dispatcher, "Ctrl+Shift+P", typing).Command!.Id);
    }

    [Fact]
    public void CMD05_PlainNavigationKeysNeedThePageSurfaceNotJustNoTextBox()
    {
        var (dispatcher, _) = Make();
        var list = new DispatchInput { Context = Doc, SurfaceFocused = false }; // a thumbnail list has focus: the arrows are its own
        Assert.Equal(DispatchKind.NotHandled, Press(dispatcher, "Right", list).Kind);
        Assert.Equal(DispatchKind.NotHandled, Press(dispatcher, "Home", list).Kind);
        Assert.Equal(DispatchKind.Execute, Press(dispatcher, "Ctrl+1", list).Kind); // but a Ctrl chord is a command wherever no text is being typed
        Assert.Equal("nav.nextPage", Press(dispatcher, "Right").Command!.Id);
    }

    [Fact]
    public void CMD05_AChordNoCommandHoldsIsNotHandled()
    {
        var (dispatcher, _) = Make();
        Assert.Equal(DispatchKind.NotHandled, Press(dispatcher, "Ctrl+Shift+Right").Kind);
        Assert.Equal(DispatchKind.NotHandled, Press(dispatcher, "Shift+Left").Kind); // the surface's own selection keys
    }

    [Fact]
    public void CMD06_WithAltGrActiveCtrlAltChordsDoNotFire()
    {
        var (dispatcher, _) = Make();
        var chord = "Ctrl+Alt+0"; // view.zoomPhysical
        Assert.Equal(DispatchKind.Execute, Press(dispatcher, chord).Kind);
        Assert.Equal(DispatchKind.NotHandled, Press(dispatcher, chord, new DispatchInput { Context = Doc, SurfaceFocused = true, AltGrActive = true }).Kind);
        Assert.Equal(DispatchKind.Execute, Press(dispatcher, "Ctrl+1", new DispatchInput { Context = Doc, SurfaceFocused = true, AltGrActive = true }).Kind);
    }

    [Fact]
    public void CMD07_AutoRepeatRunsARepeatableCommandEveryTimeAndAOneShotCommandOnce()
    {
        var (dispatcher, _) = Make();
        int Runs(string chord)
        {
            var runs = 0;
            for (var i = 0; i < 30; i++)
            {
                var result = Press(dispatcher, chord, new DispatchInput { Context = Doc, SurfaceFocused = true, IsRepeat = i > 0 });
                Assert.NotEqual(DispatchKind.NotHandled, result.Kind); // handled either way, so the focused control does not act on the repeats
                runs += result.Kind == DispatchKind.Execute ? 1 : 0;
            }

            return runs;
        }

        Assert.Equal(30, Runs("Ctrl+Plus")); // zoom is repeatable
        Assert.Equal(30, Runs("PageDown")); // so is scrolling
        Assert.Equal(1, Runs("Ctrl+1")); // fit page runs once per press
        Assert.Equal(1, Runs("Ctrl+F"));
        Assert.Equal(1, Runs("F11"));
    }

    [Fact]
    public void CMD08_ADisabledCommandIsHandledAndSaysWhyOncePerPress()
    {
        var (dispatcher, _) = Make();
        var none = new DispatchInput { Context = CommandContext.Empty, SurfaceFocused = true };
        var first = Press(dispatcher, "Ctrl+Plus", none);
        Assert.Equal(DispatchKind.Disabled, first.Kind);
        Assert.Equal(Reasons.NoDocument, first.Availability.Reason);
        var repeat = Press(dispatcher, "Ctrl+Plus", new DispatchInput { Context = CommandContext.Empty, SurfaceFocused = true, IsRepeat = true });
        Assert.Equal(DispatchKind.Swallow, repeat.Kind);
    }

    [Fact]
    public void CMD05_ANavigationKeyIsLeftForTheFocusedControlOnceTheCommandIsUnbound()
    {
        var (dispatcher, registry) = Make();
        registry.SetChords("nav.nextPage", [], reassign: false);
        Assert.Equal(DispatchKind.NotHandled, Press(dispatcher, "Right").Kind); // the surface has no key handler of its own for it any more
        Assert.Equal(DispatchKind.NotHandled, Press(dispatcher, "Right", new DispatchInput { Context = Doc, SurfaceFocused = false }).Kind);
        registry.Reset("nav.nextPage");
        Assert.Equal(DispatchKind.Execute, Press(dispatcher, "Right").Kind);
    }

    [Fact]
    public void CMD05_ARemappedChordRunsTheCommandAndTheOldOneNoLonger()
    {
        var (dispatcher, registry) = Make();
        registry.SetChords("view.zoomIn", [ChordParser.Parse("Ctrl+Alt+Z")], reassign: false);
        Assert.Equal("view.zoomIn", Press(dispatcher, "Ctrl+Alt+Z").Command!.Id);
        Assert.Equal(DispatchKind.NotHandled, Press(dispatcher, "Ctrl+Plus").Kind);
    }

    [Fact]
    public void CMD26_WhileAnInputMethodComposesNoChordFires()
    {
        var (dispatcher, _) = Make();
        Assert.Equal(DispatchKind.NotHandled, Press(dispatcher, "Ctrl+F", new DispatchInput { Context = Doc, ImeComposing = true }).Kind);
    }

    [Fact]
    public void CMD12_WithThePaletteOpenOnlyItsOwnShortcutWorks()
    {
        var (dispatcher, _) = Make();
        var open = new DispatchInput { Context = Doc, TextInputFocused = true, PaletteOpen = true };
        Assert.Equal("app.commandPalette", Press(dispatcher, "Ctrl+Shift+P", open).Command!.Id);
        Assert.Equal(DispatchKind.NotHandled, Press(dispatcher, "Ctrl+F", open).Kind);
        Assert.Equal(DispatchKind.NotHandled, Press(dispatcher, "F11", open).Kind);
    }

    [Fact]
    public void CMD29_InPresentationModeOnlyTheListedCommandsRunAndThePaletteSaysWhyNot()
    {
        var (dispatcher, _) = Make();
        var presenting = new DispatchInput { Context = Doc with { Presentation = true }, SurfaceFocused = true };
        var palette = Press(dispatcher, "Ctrl+Shift+P", presenting);
        Assert.Equal(DispatchKind.Disabled, palette.Kind);
        Assert.Equal(Reasons.Presentation, palette.Availability.Reason);
        Assert.Equal(DispatchKind.Disabled, Press(dispatcher, "Ctrl+Plus", presenting).Kind);
        Assert.Equal(DispatchKind.Execute, Press(dispatcher, "F11", presenting).Kind);
    }

    [Fact]
    public void EditingScopeCommandsWinInsideATextEditorAndOnlyThere()
    {
        var registry = new CommandRegistry([
            new CommandDefinition("edit.commitText", CommandCategory.Edit, CommandScope.Editing, string.Empty, [ChordParser.Parse("Ctrl+Enter")], _ => CommandAvailability.Enabled),
            new CommandDefinition("edit.enterThing", CommandCategory.Edit, CommandScope.Editing, string.Empty, [ChordParser.Parse("Enter")], _ => CommandAvailability.Enabled),
        ]);
        var dispatcher = new ChordDispatcher(registry);
        var editing = new DispatchInput { TextInputFocused = true, EditingActive = true };
        Assert.Equal("edit.enterThing", dispatcher.Dispatch(ChordParser.Parse("Enter"), editing).Command!.Id);
        Assert.Equal(DispatchKind.NotHandled, dispatcher.Dispatch(ChordParser.Parse("Enter"), editing with { EditingActive = false }).Kind);
    }
}
