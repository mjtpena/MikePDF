using Microsoft.UI.Xaml.Controls;
using MikePDF.Broker;
using MikePDF.Core.Windowing;

namespace MikePDF.App.Services;

/// <summary>
/// The live side of a tab: its <see cref="TabModel"/> (plain data), the <see cref="DocumentSession"/> that shows it, the strip item, and the leases that keep its engine from being
/// suspended while it is on screen or a job runs. The session news is relayed to whichever window owns the tab now, so moving the tab to another window moves the news with it.
/// </summary>
internal sealed class TabEntry
{
    private IDisposable? _pin;

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
