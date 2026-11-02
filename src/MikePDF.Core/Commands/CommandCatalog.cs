using System.Collections.Frozen;

namespace MikePDF.Core.Commands;

/// <summary>
/// Every command the app has today (docs/design/commands.md is the inventory of ids). Ids are never reused or renamed: tests/MikePDF.Core.Tests/command-ids.txt
/// is the committed snapshot and a removal fails the build. A command is added here in the same change that gives it an action in the window, so the palette
/// never lists something that does nothing (design principle: no placeholder UI for unbuilt features).
/// </summary>
public static class CommandCatalog
{
    private static readonly CommandAvailability Ok = CommandAvailability.Enabled;

    public static IReadOnlyList<CommandDefinition> All { get; } = Build();

    public static FrozenDictionary<string, CommandDefinition> ById { get; } = All.ToFrozenDictionary(c => c.Id, StringComparer.Ordinal);

    private static CommandAvailability Fail(string reason) => CommandAvailability.Disabled(reason);

    private static CommandAvailability NeedsDocument(CommandContext c) => c.HasDocument ? Ok : Fail(Reasons.NoDocument);

    private static CommandAvailability Always(CommandContext c) => Ok;

    private static CommandAvailability NeedsImportExport(CommandContext c) => c.ImportExportAllowed ? Ok : Fail(Reasons.Policy);

    private static CommandDefinition Define(
        string id,
        CommandCategory category,
        CommandScope scope,
        string glyph,
        string[] chords,
        Func<CommandContext, CommandAvailability> rule,
        bool repeatable = false,
        bool surfaceNative = false,
        bool keywords = false,
        bool dialog = false,
        bool inPresentation = false) =>
        new(id, category, scope, glyph, chords.Select(ChordParser.Parse), rule)
        {
            Repeatable = repeatable,
            SurfaceNative = surfaceNative,
            HasKeywords = keywords,
            OpensDialog = dialog,
            AllowedInPresentation = inPresentation,
        };

    private static List<CommandDefinition> Build()
    {
        var file = CommandCategory.File;
        var view = CommandCategory.View;
        var nav = CommandCategory.Navigate;
        var settings = CommandCategory.Settings;
        var help = CommandCategory.Help;
        const CommandScope global = CommandScope.Global;
        const CommandScope reader = CommandScope.Reader;

        return
        [
            // ---- file
            Define("file.open", file, global, "", ["Ctrl+O"], Always, dialog: true, keywords: true),
            Define("file.reload", file, global, "", ["Ctrl+R"], c => c.Busy ? Fail(Reasons.Busy) : !c.HasDocument ? Fail(Reasons.NoDocument) : c.HasPath ? Ok : Fail(Reasons.NoPath), keywords: true),
            Define("file.properties", file, global, "", ["Ctrl+D"], NeedsDocument, keywords: true),

            // ---- navigation
            Define("nav.back", nav, global, "", ["Alt+Left"], c => !c.HasDocument ? Fail(Reasons.NoDocument) : c.CanGoBack ? Ok : Fail(Reasons.NoBackHistory)),
            Define("nav.forward", nav, global, "", ["Alt+Right"], c => !c.HasDocument ? Fail(Reasons.NoDocument) : c.CanGoForward ? Ok : Fail(Reasons.NoForwardHistory)),
            Define("nav.goToPage", nav, reader, "", ["Ctrl+G"], NeedsDocument, dialog: true, keywords: true),
            Define("nav.nextPage", nav, reader, "", ["Right"], NeedsDocument, repeatable: true, surfaceNative: true),
            Define("nav.previousPage", nav, reader, "", ["Left"], NeedsDocument, repeatable: true, surfaceNative: true),
            Define("nav.firstPage", nav, reader, "", ["Home"], NeedsDocument, surfaceNative: true),
            Define("nav.lastPage", nav, reader, "", ["End"], NeedsDocument, surfaceNative: true),
            Define("nav.scrollScreenDown", nav, reader, "", ["PageDown", "Space"], NeedsDocument, repeatable: true, surfaceNative: true),
            Define("nav.scrollScreenUp", nav, reader, "", ["PageUp", "Shift+Space"], NeedsDocument, repeatable: true, surfaceNative: true),
            Define("nav.scrollDown", nav, reader, "", ["Down"], NeedsDocument, repeatable: true, surfaceNative: true),
            Define("nav.scrollUp", nav, reader, "", ["Up"], NeedsDocument, repeatable: true, surfaceNative: true),

            // ---- view
            Define("view.zoomIn", view, reader, "", ["Ctrl+Plus", "Ctrl+Add"], NeedsDocument, repeatable: true, keywords: true),
            Define("view.zoomOut", view, reader, "", ["Ctrl+Minus", "Ctrl+Subtract"], NeedsDocument, repeatable: true, keywords: true),
            Define("view.zoomActual", view, reader, "", ["Ctrl+0", "Ctrl+NumberPad0"], NeedsDocument, keywords: true),
            Define("view.zoomPhysical", view, reader, "", ["Ctrl+Alt+0"], NeedsDocument, keywords: true),
            Define("view.fitWidth", view, reader, "", ["Ctrl+2"], NeedsDocument),
            Define("view.fitPage", view, reader, "", ["Ctrl+1"], NeedsDocument),
            Define("view.zoomTo", view, reader, "", [], NeedsDocument, dialog: true),
            Define("view.rotateRight", view, reader, "", ["Ctrl+Shift+Plus"], NeedsDocument, keywords: true),
            Define("view.rotateLeft", view, reader, "", ["Ctrl+Shift+Minus"], NeedsDocument, keywords: true),
            Define("view.readingMode", view, global, "", ["F11"], Always, keywords: true, inPresentation: true),
            Define("view.pageColors", view, reader, "", ["Ctrl+Shift+I"], Always, keywords: true),
            Define("view.pageColorsNormal", view, reader, "", [], Always),
            Define("view.pageColorsDark", view, reader, "", [], Always, keywords: true),
            Define("view.pageColorsSepia", view, reader, "", [], Always),

            // ---- panes
            Define("pane.toggleLeft", view, global, "", ["F4"], NeedsDocument, keywords: true),
            Define("pane.thumbnails", view, global, "", ["Alt+Shift+T"], NeedsDocument),
            Define("pane.outline", view, global, "", ["Alt+Shift+B"], NeedsDocument, keywords: true),

            // ---- find and selection
            Define("find.open", nav, global, "", ["Ctrl+F"], NeedsDocument, keywords: true),
            Define("find.next", nav, global, "", ["F3"], NeedsDocument, repeatable: true),
            Define("find.previous", nav, global, "", ["Shift+F3"], NeedsDocument, repeatable: true),
            Define("find.close", nav, global, "", [], c => c.FindOpen ? Ok : Fail(Reasons.FindNotOpen)),
            Define("edit.selectAll", CommandCategory.Edit, reader, "", ["Ctrl+A"], NeedsDocument, surfaceNative: true),
            Define("edit.copy", CommandCategory.Edit, reader, "", ["Ctrl+C", "Ctrl+Insert"], c => !c.HasDocument ? Fail(Reasons.NoDocument) : c.CanCopy ? Ok : Fail(Reasons.OwnerNoCopy), surfaceNative: true),

            // ---- application
            Define("app.commandPalette", settings, global, "", ["Ctrl+Shift+P"], c => c.ModalOpen ? Fail(Reasons.Busy) : Ok, keywords: true),
            Define("app.settings", settings, global, "", ["Ctrl+Comma"], Always, keywords: true),
            Define("app.shortcuts", help, global, "", ["Ctrl+Slash", "F1"], Always, keywords: true),
            Define("app.about", help, global, "", [], Always, dialog: true),
            Define("app.policies", help, global, "", [], Always, keywords: true),
            Define("app.clearRecent", settings, global, "", [], c => c.HasRecent ? Ok : Fail(Reasons.NoRecent), dialog: true),
            Define("app.clearHistory", settings, global, "", [], Always, dialog: true),
            Define("app.clearUsageHistory", settings, global, "", [], c => c.HasUsageHistory ? Ok : Fail(Reasons.NothingToClear)),
            Define("app.exportSettings", settings, global, "", [], NeedsImportExport, dialog: true),
            Define("app.importSettings", settings, global, "", [], NeedsImportExport, dialog: true),
            Define("app.undoImportSettings", settings, global, "", [], c => !c.ImportExportAllowed ? Fail(Reasons.Policy) : c.CanUndoImport ? Ok : Fail(Reasons.NothingToUndo)),
            Define("app.resetSettings", settings, global, "", [], Always, dialog: true),
            Define("app.changeShortcut", settings, global, "", [], Always, dialog: true),
            Define("app.exportShortcuts", settings, global, "", [], NeedsImportExport, dialog: true),
            Define("app.importShortcuts", settings, global, "", [], NeedsImportExport, dialog: true),
            Define("app.resetShortcuts", settings, global, "", [], c => c.HasCustomShortcuts ? Ok : Fail(Reasons.NoCustomShortcuts), dialog: true),
            Define("app.copyShortcuts", settings, global, "", [], Always),
        ];
    }
}
