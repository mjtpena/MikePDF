using System.Collections.Immutable;
using System.Text.RegularExpressions;
using MikePDF.Core.Colour;

namespace MikePDF.Core.Commands;

/// <summary>Where a command's shortcut works (docs/features/command-palette-shortcuts.md 3.4).</summary>
public enum CommandScope
{
    /// <summary>Any focus, except that a text input keeps the text-editing chords.</summary>
    Global,

    /// <summary>The page surface has focus, or no text input does. Plain keys (no Ctrl or Alt) need the page surface itself.</summary>
    Reader,

    /// <summary>An annotation or form text editor is active. Nothing uses it yet; the scope exists so later features need no change here.</summary>
    Editing,

    /// <summary>Presentation mode only.</summary>
    Presentation,
}

public enum CommandCategory
{
    File,
    Edit,
    View,
    Navigate,
    TabsAndWindows,
    Markup,
    Pen,
    Forms,
    Pages,
    Tools,
    Settings,
    Help,
}

/// <summary>The reason keys a disabled command can give. Each has a string "Reason_{key}" in the resources.</summary>
public static class Reasons
{
    public const string NoDocument = "NoDocument";
    public const string NoSelection = "NoSelection";
    public const string OwnerNoCopy = "OwnerNoCopy";
    public const string Policy = "Policy";
    public const string Busy = "Busy";
    public const string Presentation = "Presentation";
    public const string NoBackHistory = "NoBackHistory";
    public const string NoForwardHistory = "NoForwardHistory";
    public const string NoRecent = "NoRecent";
    public const string NoPath = "NoPath";
    public const string NoCustomShortcuts = "NoCustomShortcuts";
    public const string NothingToUndo = "NothingToUndo";
    public const string NothingToClear = "NothingToClear";
    public const string FindNotOpen = "FindNotOpen";
    public const string OneTab = "OneTab";
    public const string NoSuchTab = "NoSuchTab";
    public const string FirstTab = "FirstTab";
    public const string LastTab = "LastTab";
    public const string NothingClosed = "NothingClosed";
    public const string NotMissing = "NotMissing";
    public const string NotFacing = "NotFacing";
    public const string NotPresenting = "NotPresenting";
    public const string OwnerNoPrint = "OwnerNoPrint";
    public const string PrintBusy = "PrintBusy";
    public const string NotPrinting = "NotPrinting";
    public const string NoEdits = "NoEdits";
    public const string SaveBusy = "SaveBusy";

    public static string ResourceKey(string reason) => "Reason_" + reason;
}

/// <summary>Whether a command can run now, and if it cannot, why (a key from <see cref="Reasons"/>).</summary>
public readonly record struct CommandAvailability(bool IsEnabled, string? Reason)
{
    public static CommandAvailability Enabled { get; } = new(true, null);

    public static CommandAvailability Disabled(string reason) => new(false, reason);
}

/// <summary>
/// An immutable snapshot of what the window knows, built by the window for each question about commands. <c>CanExecute</c> reads only this, never XAML or the
/// engine, so every rule is a plain unit test.
/// </summary>
public sealed record CommandContext
{
    public static CommandContext Empty { get; } = new();

    public bool HasDocument { get; init; }

    public int PageCount { get; init; }

    public int CurrentPage { get; init; }

    public bool HasPath { get; init; }

    public bool CanGoBack { get; init; }

    public bool CanGoForward { get; init; }

    /// <summary>The owner allows copying text (permission bit 5). True when there is no document: the question does not arise.</summary>
    public bool CanCopy { get; init; } = true;

    public bool HasSelection { get; init; }

    public bool FindOpen { get; init; }

    public bool FullScreen { get; init; }

    public bool SidebarOpen { get; init; }

    public bool Busy { get; init; }

    public bool Presentation { get; init; }

    public bool SettingsOpen { get; init; }

    /// <summary>A modal dialog is open.</summary>
    public bool ModalOpen { get; init; }

    public bool HasRecent { get; init; }

    public bool HasUsageHistory { get; init; }

    public bool HasCustomShortcuts { get; init; }

    public bool CanUndoImport { get; init; }

    /// <summary>How many tabs the window has, and which is shown (0 based). A window always has at least one.</summary>
    public int TabCount { get; init; } = 1;

    public int ActiveTabIndex { get; init; }

    /// <summary>A closed tab is remembered and Reopen closed tab has something to open.</summary>
    public bool CanReopenClosed { get; init; }

    /// <summary>The shown tab is a restored document whose file could not be found (Locate file).</summary>
    public bool TabMissing { get; init; }

    public PageColourMode PageColour { get; init; }

    /// <summary>How pages are arranged and turned in the shown document (the checked items of the View menu).</summary>
    public MikePDF.Core.Layout.ViewMode ViewMode { get; init; }

    /// <summary>Policy: settings and shortcut import and export are allowed (SettingsImportExportEnabled). True when no policy says otherwise.</summary>
    public bool ImportExportAllowed { get; init; } = true;

    /// <summary>Policy: printing is allowed (PrintingEnabled). True when no policy says otherwise.</summary>
    public bool PrintingAllowed { get; init; } = true;

    /// <summary>The document's owner allows printing (permission bit 3). True when there is no document or no protection: the question does not arise.</summary>
    public bool OwnerAllowsPrinting { get; init; } = true;

    /// <summary>A print job started from this window is running (Cancel printing can act; a second Print waits).</summary>
    public bool PrintJobRunning { get; init; }

    /// <summary>The shown document has edits that are not in its file (Save can act; the modified marker is on).</summary>
    public bool HasEdits { get; init; }

    /// <summary>A save of the shown document is running: the document is read-only until it ends, and a second save waits (save-pipeline 3.2).</summary>
    public bool SaveRunning { get; init; }
}

/// <summary>
/// One command: stable id, category, default chords, scope, glyph and the rule that says when it can run. Its name, and optional keywords for the palette,
/// are resources named from the id (<see cref="NameKey"/>). Executing it is the window's business (the definitions are data, so they can be tested without a window).
/// </summary>
public sealed class CommandDefinition
{
    public static readonly Regex IdPattern = new(@"^[a-z][A-Za-z0-9]*(\.[a-z][A-Za-z0-9]*)+$", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public CommandDefinition(string id, CommandCategory category, CommandScope scope, string glyph, IEnumerable<Chord> defaultChords, Func<CommandContext, CommandAvailability> rule)
    {
        Id = id;
        Category = category;
        Scope = scope;
        Glyph = glyph;
        DefaultChords = [.. defaultChords];
        Rule = rule;
    }

    public string Id { get; }

    public CommandCategory Category { get; }

    public CommandScope Scope { get; }

    /// <summary>A Segoe Fluent Icons glyph, or empty.</summary>
    public string Glyph { get; }

    public ImmutableArray<Chord> DefaultChords { get; }

    /// <summary>True for commands that make sense on key auto-repeat: zoom, scroll, next page.</summary>
    public bool Repeatable { get; init; }

    /// <summary>Still runs in presentation mode (exit, quit, tabs).</summary>
    public bool AllowedInPresentation { get; init; }

    /// <summary>Has a <see cref="KeywordsKey"/> resource: extra words the palette also matches (a synonym, "magnify" for Zoom in).</summary>
    public bool HasKeywords { get; init; }

    /// <summary>Opens a dialog or needs more input: the name ends with an ellipsis in the palette.</summary>
    public bool OpensDialog { get; init; }

    internal Func<CommandContext, CommandAvailability> Rule { get; }

    public string NameKey => "Cmd_" + Id.Replace('.', '_') + "_Name";

    public string KeywordsKey => "Cmd_" + Id.Replace('.', '_') + "_Keywords";

    public static string CategoryKey(CommandCategory category) => "Category_" + category;

    /// <summary>The answer for a context: presentation and busy rules first, then the command's own.</summary>
    public CommandAvailability CanExecute(CommandContext context)
    {
        if (context.Presentation && Scope != CommandScope.Presentation && !AllowedInPresentation)
        {
            return CommandAvailability.Disabled(Reasons.Presentation);
        }

        return Rule(context);
    }

    public override string ToString() => Id;
}
