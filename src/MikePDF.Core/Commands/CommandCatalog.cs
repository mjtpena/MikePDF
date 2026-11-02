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

    private static CommandAvailability NeedsPresenting(CommandContext c) => c.Presentation ? Ok : Fail(Reasons.NotPresenting);

    private static CommandAvailability NeedsTwoTabs(CommandContext c) => c.TabCount >= 2 ? Ok : Fail(Reasons.OneTab);

    private static Func<CommandContext, CommandAvailability> NeedsTab(int number) => c => c.TabCount >= number ? Ok : Fail(Reasons.NoSuchTab);

    private static CommandAvailability NeedsPath(CommandContext c) => !c.HasDocument ? Fail(Reasons.NoDocument) : c.HasPath ? Ok : Fail(Reasons.NoPath);

    // Print: a document, then the organisation's policy (an administrator who turned printing off does not want the document's reason shown instead), then the owner's permission (print.md 3.7,
    // PRINT-22), then one job at a time per window.
    private static CommandAvailability CanPrint(CommandContext c) =>
        !c.HasDocument ? Fail(Reasons.NoDocument)
        : !c.PrintingAllowed ? Fail(Reasons.Policy)
        : !c.OwnerAllowsPrinting ? Fail(Reasons.OwnerNoPrint)
        : c.PrintJobRunning ? Fail(Reasons.PrintBusy)
        : Ok;

    private static CommandAvailability NeedsImportExport(CommandContext c) => c.ImportExportAllowed ? Ok : Fail(Reasons.Policy);

    private static CommandDefinition Define(
        string id,
        CommandCategory category,
        CommandScope scope,
        string glyph,
        string[] chords,
        Func<CommandContext, CommandAvailability> rule,
        bool repeatable = false,
        bool keywords = false,
        bool dialog = false,
        bool inPresentation = false) =>
        new(id, category, scope, glyph, chords.Select(ChordParser.Parse), rule)
        {
            Repeatable = repeatable,
            HasKeywords = keywords,
            OpensDialog = dialog,
            AllowedInPresentation = inPresentation,
        };

    private static List<CommandDefinition> Build()
    {
        var file = CommandCategory.File;
        var view = CommandCategory.View;
        var nav = CommandCategory.Navigate;
        var tabs = CommandCategory.TabsAndWindows;
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
            Define("file.print", file, global, "", ["Ctrl+P"], CanPrint, keywords: true, dialog: true),
            Define("file.printCurrentPage", file, global, "", [], CanPrint, keywords: true, dialog: true),
            Define("print.cancel", file, global, "", [], c => c.PrintJobRunning ? Ok : Fail(Reasons.NotPrinting), keywords: true, inPresentation: true),
            Define("file.newTab", file, global, "", ["Ctrl+T"], Always, keywords: true, inPresentation: true),
            Define("file.newWindow", file, global, "", ["Ctrl+N"], Always, keywords: true, inPresentation: true),
            Define("file.close", file, global, "", ["Ctrl+W", "Ctrl+F4"], Always, keywords: true, inPresentation: true),
            Define("file.closeOthers", file, global, "", [], NeedsTwoTabs),
            Define("file.closeToRight", file, global, "", [], c => c.ActiveTabIndex < c.TabCount - 1 ? Ok : Fail(Reasons.LastTab)),
            Define("file.reopenClosed", file, global, "", ["Ctrl+Shift+T"], c => c.CanReopenClosed ? Ok : Fail(Reasons.NothingClosed), keywords: true, inPresentation: true),
            Define("file.closeWindow", file, global, "", [], Always, inPresentation: true),
            Define("file.copyPath", file, global, "", [], NeedsPath),
            Define("file.showInFolder", file, global, "", [], NeedsPath, keywords: true),
            Define("file.locate", file, global, "", [], c => c.TabMissing ? Ok : Fail(Reasons.NotMissing), dialog: true),

            // ---- tabs and windows
            Define("tab.next", tabs, global, "", ["Ctrl+Tab"], NeedsTwoTabs, inPresentation: true),
            Define("tab.previous", tabs, global, "", ["Ctrl+Shift+Tab"], NeedsTwoTabs, inPresentation: true),
            Define("tab.goto1", tabs, global, "", ["Alt+1"], NeedsTab(1), inPresentation: true),
            Define("tab.goto2", tabs, global, "", ["Alt+2"], NeedsTab(2), inPresentation: true),
            Define("tab.goto3", tabs, global, "", ["Alt+3"], NeedsTab(3), inPresentation: true),
            Define("tab.goto4", tabs, global, "", ["Alt+4"], NeedsTab(4), inPresentation: true),
            Define("tab.goto5", tabs, global, "", ["Alt+5"], NeedsTab(5), inPresentation: true),
            Define("tab.goto6", tabs, global, "", ["Alt+6"], NeedsTab(6), inPresentation: true),
            Define("tab.goto7", tabs, global, "", ["Alt+7"], NeedsTab(7), inPresentation: true),
            Define("tab.goto8", tabs, global, "", ["Alt+8"], NeedsTab(8), inPresentation: true),
            Define("tab.last", tabs, global, "", ["Alt+9"], Always, inPresentation: true),
            Define("tab.moveLeft", tabs, global, "", ["Ctrl+Shift+PageUp"], c => c.ActiveTabIndex > 0 ? Ok : Fail(Reasons.FirstTab)),
            Define("tab.moveRight", tabs, global, "", ["Ctrl+Shift+PageDown"], c => c.ActiveTabIndex < c.TabCount - 1 ? Ok : Fail(Reasons.LastTab)),
            Define("tab.moveToNewWindow", tabs, global, "", ["Ctrl+Shift+N"], NeedsTwoTabs, keywords: true),

            // ---- navigation
            Define("nav.back", nav, global, "", ["Alt+Left"], c => !c.HasDocument ? Fail(Reasons.NoDocument) : c.CanGoBack ? Ok : Fail(Reasons.NoBackHistory)),
            Define("nav.forward", nav, global, "", ["Alt+Right"], c => !c.HasDocument ? Fail(Reasons.NoDocument) : c.CanGoForward ? Ok : Fail(Reasons.NoForwardHistory)),
            Define("nav.goToPage", nav, reader, "", ["Ctrl+G"], NeedsDocument, dialog: true, keywords: true),
            Define("nav.nextPage", nav, reader, "", ["Right"], NeedsDocument, repeatable: true, inPresentation: true),
            Define("nav.previousPage", nav, reader, "", ["Left"], NeedsDocument, repeatable: true, inPresentation: true),
            Define("nav.firstPage", nav, reader, "", ["Home"], NeedsDocument, inPresentation: true),
            Define("nav.lastPage", nav, reader, "", ["End"], NeedsDocument, inPresentation: true),
            Define("nav.scrollScreenDown", nav, reader, "", ["PageDown", "Space"], NeedsDocument, repeatable: true, inPresentation: true),
            Define("nav.scrollScreenUp", nav, reader, "", ["PageUp", "Shift+Space"], NeedsDocument, repeatable: true, inPresentation: true),
            Define("nav.scrollDown", nav, reader, "", ["Down"], NeedsDocument, repeatable: true, inPresentation: true),
            Define("nav.scrollUp", nav, reader, "", ["Up"], NeedsDocument, repeatable: true, inPresentation: true),

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
            Define("view.layoutSingle", view, reader, "", ["Ctrl+4"], NeedsDocument, keywords: true),
            Define("view.layoutTwo", view, reader, "", ["Ctrl+5"], NeedsDocument, keywords: true),
            Define("view.layoutTwoCover", view, reader, "", ["Ctrl+6"], NeedsDocument, keywords: true),
            Define("view.continuous", view, reader, "", ["Ctrl+7"], NeedsDocument, keywords: true),
            Define("view.coverPage", view, reader, "", [], c => !c.HasDocument ? Fail(Reasons.NoDocument) : c.ViewMode.IsFacing ? Ok : Fail(Reasons.NotFacing), keywords: true),
            Define("view.directionAuto", view, reader, "", [], NeedsDocument),
            Define("view.directionLtr", view, reader, "", [], NeedsDocument, keywords: true),
            Define("view.directionRtl", view, reader, "", [], NeedsDocument, keywords: true),
            Define("view.presentation", view, global, "", ["Ctrl+L"], c => !c.HasDocument ? Fail(Reasons.NoDocument) : c.ModalOpen ? Fail(Reasons.Busy) : Ok, keywords: true, inPresentation: true),
            Define("present.next", view, CommandScope.Presentation, "", ["Enter", "N"], NeedsPresenting),
            Define("present.previous", view, CommandScope.Presentation, "", ["Backspace", "P"], NeedsPresenting),
            Define("present.black", view, CommandScope.Presentation, "", ["B", "Period"], NeedsPresenting),
            Define("present.white", view, CommandScope.Presentation, "", ["W", "Comma"], NeedsPresenting),
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
            Define("edit.selectAll", CommandCategory.Edit, reader, "", ["Ctrl+A"], NeedsDocument),
            Define("edit.copy", CommandCategory.Edit, reader, "", ["Ctrl+C", "Ctrl+Insert"], c => !c.HasDocument ? Fail(Reasons.NoDocument) : c.CanCopy ? Ok : Fail(Reasons.OwnerNoCopy)),

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
