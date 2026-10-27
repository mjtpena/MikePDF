using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Automation.Text;
using S7.Core.Text;
using Windows.Foundation;
using CoreUnit = S7.Core.Text.TextUnitKind;
using CoreEnd = S7.Core.Text.RangeEndpoint;

namespace PageA11y;

/// <summary>
/// S7.1. A FrameworkElementAutomationPeer that implements the UIA Text pattern from C#.
/// ITextProvider / ITextProvider2 / ITextRangeProvider are in Microsoft.UI.Xaml.Automation.Provider (WinAppSDK 2.5.1,
/// Microsoft.WindowsAppSDK.WinUI 2.3.9 metadata). The XAML framework wraps whatever GetPatternCore returns.
/// </summary>
public sealed class PageSurfaceAutomationPeer : FrameworkElementAutomationPeer, ITextProvider, ITextProvider2
{
    private readonly PageSurface _s;
    private int _eventsRaised;

    public PageSurfaceAutomationPeer(PageSurface owner) : base(owner)
    {
        _s = owner;
        _s.SelectionChanged += OnSelectionChanged;
        _s.TextChanged += OnTextChanged;
    }

    public int SelectionEventsRaised => _eventsRaised;

    private void OnSelectionChanged()
    {
        _eventsRaised++;
        Log.Write($"SelectionChanged -> [{_s.SelStart},{_s.SelEnd}) listener={ListenerExists(AutomationEvents.TextPatternOnTextSelectionChanged)}");
        if (ListenerExists(AutomationEvents.TextPatternOnTextSelectionChanged))
            RaiseAutomationEvent(AutomationEvents.TextPatternOnTextSelectionChanged);
    }

    private void OnTextChanged()
    {
        if (ListenerExists(AutomationEvents.TextPatternOnTextChanged))
            RaiseAutomationEvent(AutomationEvents.TextPatternOnTextChanged);
    }

    // ---- peer identity -------------------------------------------------------------------------------------------
    protected override string GetClassNameCore() => "QuirePageSurface";
    protected override string GetNameCore() => "PDF page 1";
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Document;
    protected override string GetLocalizedControlTypeCore() => "document";
    protected override bool IsKeyboardFocusableCore() => true;
    protected override bool IsContentElementCore() => true;
    protected override bool IsControlElementCore() => true;
    protected override IList<AutomationPeer> GetChildrenCore() => new List<AutomationPeer>();   // internal visuals are not exposed

    protected override object GetPatternCore(PatternInterface patternInterface)
    {
        Log.Write("GetPatternCore(" + patternInterface + ")");
        return patternInterface switch
        {
            PatternInterface.Text => this,
            PatternInterface.Text2 => this,
            _ => base.GetPatternCore(patternInterface),
        };
    }

    internal IRawElementProviderSimple SelfProvider() => ProviderFromPeer(this);

    // ---- ITextProvider ------------------------------------------------------------------------------------------
    private TextModel Model => _s.Model ?? new TextModel(Array.Empty<Atom>(), 1, 1, false, "empty");

    public ITextRangeProvider DocumentRange => new PageTextRange(this, _s, new TextRange(Model, 0, Model.Length));

    public SupportedTextSelection SupportedTextSelection => SupportedTextSelection.Single;

    public ITextRangeProvider[] GetSelection()
    {
        // A degenerate range (caret) when nothing is selected, as the Win32 edit control does.
        Log.Write($"GetSelection [{_s.SelStart},{_s.SelEnd})");
        return new ITextRangeProvider[] { new PageTextRange(this, _s, new TextRange(Model, _s.SelStart, _s.SelEnd)) };
    }

    public ITextRangeProvider[] GetVisibleRanges()
        => new ITextRangeProvider[] { DocumentRange };   // single page: all of it is the visible range (scrolling is the viewport's job in production)

    public ITextRangeProvider RangeFromChild(IRawElementProviderSimple childElement) => DocumentRange;

    public ITextRangeProvider RangeFromPoint(Point screenLocation)
    {
        var local = _s.RootToLocal(screenLocation);
        int i = Model.HitTest(local.X / _s.Zoom, local.Y / _s.Zoom, _s.ViewRotation);
        Log.Write($"RangeFromPoint screen=({screenLocation.X:F1},{screenLocation.Y:F1}) local=({local.X:F1},{local.Y:F1}) -> atom {i}");
        if (i < 0) i = 0;
        return new PageTextRange(this, _s, new TextRange(Model, i, i));
    }

    // ---- ITextProvider2 -----------------------------------------------------------------------------------------
    public ITextRangeProvider GetCaretRange(out bool isActive)
    {
        isActive = _s.FocusState != FocusState.Unfocused;
        return new PageTextRange(this, _s, new TextRange(Model, _s.SelStart, _s.SelStart));
    }

    public ITextRangeProvider RangeFromAnnotation(IRawElementProviderSimple annotationElement) => DocumentRange;
}

/// <summary>S7.2. ITextRangeProvider over the reading-ordered <see cref="TextModel"/>; the logic lives in S7.Core.Text.TextRange.</summary>
public sealed class PageTextRange : ITextRangeProvider
{
    private readonly PageSurfaceAutomationPeer _peer;
    private readonly PageSurface _s;
    internal readonly TextRange R;

    internal PageTextRange(PageSurfaceAutomationPeer peer, PageSurface s, TextRange r) { _peer = peer; _s = s; R = r; }

    private static CoreUnit U(TextUnit u) => u switch
    {
        TextUnit.Character => CoreUnit.Character,
        TextUnit.Format => CoreUnit.Format,
        TextUnit.Word => CoreUnit.Word,
        TextUnit.Line => CoreUnit.Line,
        TextUnit.Paragraph => CoreUnit.Paragraph,
        TextUnit.Page => CoreUnit.Page,
        _ => CoreUnit.Document,
    };

    private static CoreEnd E(TextPatternRangeEndpoint e) => e == TextPatternRangeEndpoint.Start ? CoreEnd.Start : CoreEnd.End;

    private static TextRange Unwrap(ITextRangeProvider p)
        => p is PageTextRange r ? r.R : throw new ArgumentException("foreign ITextRangeProvider");

    public ITextRangeProvider Clone() => new PageTextRange(_peer, _s, R.Clone());

    public bool Compare(ITextRangeProvider textRangeProvider) => R.Compare(Unwrap(textRangeProvider));

    public int CompareEndpoints(TextPatternRangeEndpoint endpoint, ITextRangeProvider targetRange, TextPatternRangeEndpoint targetEndpoint)
        => R.CompareEndpoints(E(endpoint), Unwrap(targetRange), E(targetEndpoint));

    public void ExpandToEnclosingUnit(TextUnit unit) => R.ExpandToEnclosingUnit(U(unit));

    public ITextRangeProvider FindAttribute(int attributeId, object value, bool backward) => null!;

    public ITextRangeProvider FindText(string text, bool backward, bool ignoreCase)
    {
        if (string.IsNullOrEmpty(text)) return null!;
        string hay = R.GetText();
        var cmp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        int idx = backward ? hay.LastIndexOf(text, cmp) : hay.IndexOf(text, cmp);
        if (idx < 0) return null!;
        // atoms are 1:1 with UTF-16 units except for surrogate pairs, which this spike's fixtures do not contain
        return new PageTextRange(_peer, _s, new TextRange(R.Model, R.Start + idx, R.Start + idx + text.Length));
    }

    public object GetAttributeValue(int attributeId) => DependencyProperty.UnsetValue;

    public void GetBoundingRectangles(out double[] returnValue)
    {
        var list = new List<double>();
        foreach (var local in _s.RangeRectsLocal(R.Start, R.End))
        {
            var r = _s.LocalToRoot(local);
            list.Add(r.X); list.Add(r.Y); list.Add(r.Width); list.Add(r.Height);
        }
        returnValue = list.ToArray();
        Log.Write($"GetBoundingRectangles uiThread={_s.DispatcherQueue.HasThreadAccess} [{R.Start},{R.End}) -> {returnValue.Length / 4} rect(s) first={(returnValue.Length >= 4 ? $"{returnValue[0]:F1},{returnValue[1]:F1},{returnValue[2]:F1},{returnValue[3]:F1}" : "-")}");
    }

    public IRawElementProviderSimple[] GetChildren() => Array.Empty<IRawElementProviderSimple>();

    public IRawElementProviderSimple GetEnclosingElement() => _peer.SelfProvider();

    public string GetText(int maxLength) => R.GetText(maxLength);

    public int Move(TextUnit unit, int count) => R.Move(U(unit), count);

    public void MoveEndpointByRange(TextPatternRangeEndpoint endpoint, ITextRangeProvider targetRange, TextPatternRangeEndpoint targetEndpoint)
        => R.MoveEndpointByRange(E(endpoint), Unwrap(targetRange), E(targetEndpoint));

    public int MoveEndpointByUnit(TextPatternRangeEndpoint endpoint, TextUnit unit, int count)
        => R.MoveEndpointByUnit(E(endpoint), U(unit), count);

    public void ScrollIntoView(bool alignToTop) { /* one page, no scrolling in the spike. Production: ask the viewport to reveal the first rect. */ }

    public void Select()
    {
        Log.Write($"Select uiThread={_s.DispatcherQueue.HasThreadAccess} [{R.Start},{R.End})");
        _s.SetSelection(R.Start, R.End);
    }

    public void AddToSelection() => throw new NotSupportedException("single selection only");
    public void RemoveFromSelection() => throw new NotSupportedException("single selection only");
}
