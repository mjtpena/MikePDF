using Microsoft.UI.Xaml.Controls;
using MikePDF.Broker;
using MikePDF.Broker.Saving;
using MikePDF.Core.Editing.Journal;
using MikePDF.Core.Windowing;

namespace MikePDF.App.Services;

/// <summary>
/// The live side of a tab: its <see cref="TabModel"/> (plain data), the <see cref="DocumentSession"/> that shows it, the strip item, and the leases that keep its engine from being
/// suspended while it is on screen or a job runs. The session news is relayed to whichever window owns the tab now, so moving the tab to another window moves the news with it.
/// </summary>
internal sealed class TabEntry
{
    private IDisposable? _pin;
    private Action? _editsChanged;

    public TabEntry(TabModel model)
    {
        Model = model;
        Leases.PinnedChanged += pinned => ApplyPin(pinned);
    }

    public TabModel Model { get; }

    /// <summary>The window that shows the tab now. Null while it is between windows.</summary>
    public MainWindow? Owner { get; set; }

    public DocumentSession? Session { get; private set; }

    public TabViewItem Item { get; set; } = null!;

    public TabEngineLeases Leases { get; } = new();

    /// <summary>Held while the tab is shown (not minimised): the engine is pinned for as long.</summary>
    public IDisposable? ShownLease { get; set; }

    /// <summary>The document is being opened (a ring in the tab).</summary>
    public bool Opening { get; set; }

    /// <summary>A restored tab whose file could not be found: it shows the placeholder with Locate file and Close tab.</summary>
    public bool Missing { get; set; }

    /// <summary>The words (a resource key) the placeholder says when a restored tab could not be opened for another reason (too many documents).</summary>
    public string? PlaceholderKey { get; set; }

    /// <summary>The last engine news for this tab, shown when the tab is next shown (a background tab's restart is not announced in a window that shows another tab).</summary>
    public EngineBanner? Banner { get; set; }

    /// <summary>
    /// The document's edits and their journal. They belong to the tab, not to the session: a reload after a save, and a move to another window, keep them. Null until the document has opened.
    /// </summary>
    public EditSession? Edits { get; private set; }

    /// <summary>The file at the document's path as it was when the document was opened or last saved: what "changed on disk" is measured against.</summary>
    public FileStamp? Stamp { get; set; }

    /// <summary>The path was found to name a changed (or gone) file, and the person has not decided yet: the banner is up.</summary>
    public ExternalChange Changed { get; set; }

    /// <summary>The person chose "Keep what I have" for the file as it is now: the banner stays away until the file changes again.</summary>
    public FileStamp? KeptStamp { get; set; }

    /// <summary>The save that is running for this tab, if any.</summary>
    public SaveRun? SaveRun { get; set; }

    /// <summary>The fake edit of the test seam has been applied (it is applied once, not again after a reload).</summary>
    public bool FakeEditApplied { get; set; }

    public DocumentEngineState EngineState => Session?.EngineState ?? DocumentEngineState.Running;

    public void SetSession(DocumentSession? session)
    {
        _pin?.Dispose();
        _pin = null;
        Session = session;
        if (session is null)
        {
            return;
        }

        session.ViewChanged += () => Owner?.OnEntryViewChanged(this);
        session.EngineStateChanged += state =>
        {
            Banner = state;
            Owner?.OnEntryBanner(this, state);
        };
        session.PageIncomplete += page => Owner?.OnEntryPageIncomplete(this, page);
        session.OverlaysChanged += () => Owner?.OnEntryOverlays(this);
        session.EngineLifeChanged += state => Owner?.OnEntryEngineLife(this, state);
    }

    /// <summary>Gives the tab its edits. <paramref name="changed"/> runs whenever they change (dirty state, a save starting or ending).</summary>
    public void SetEdits(EditSession edits, Action changed)
    {
        ReleaseEdits(discard: false);
        Edits = edits;
        _editsChanged = changed;
        edits.Model.Changed += changed;
        Model.IsDirty = edits.Model.IsDirty;
    }

    /// <summary>
    /// The tab is going away (or its edits are being dropped). <paramref name="discard"/> is "Don't save": the journal is deleted. Without it the journal stays, which is what a crash, a shutdown or a
    /// move to another place needs.
    /// </summary>
    public void ReleaseEdits(bool discard)
    {
        if (Edits is not { } edits)
        {
            return;
        }

        if (_editsChanged is not null)
        {
            edits.Model.Changed -= _editsChanged;
            _editsChanged = null;
        }

        if (discard)
        {
            edits.ClearJournal();
        }

        edits.Dispose();
        Edits = null;
        Model.IsDirty = false;
    }

    /// <summary>The document is open: if the tab is already pinned (it is on screen) the engine is pinned now.</summary>
    public void PinIfNeeded()
    {
        if (Leases.IsPinned)
        {
            ApplyPin(true);
        }
    }

    private void ApplyPin(bool pinned)
    {
        if (pinned)
        {
            _pin ??= Session?.PinEngine();
        }
        else
        {
            _pin?.Dispose();
            _pin = null;
        }
    }
}
