using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Automation.Text;
using MikePDF.Core.Text;
using Windows.Foundation;

namespace MikePDF.App.Views;

/// <summary>
/// The automation peer of the page surface (S7): a document that implements the UI Automation Text pattern over the text of the pages in view,
/// in reading order. The pages in view are all a screen reader can read at one time (a 2,000-page document cannot offer one range of all its
/// text); when the person scrolls to other pages the text changes and a text-changed event tells the screen reader so. The model is built from
/// the engine's characters in the app process (the engine never sees UI Automation), and every provider call arrives on the UI thread, where
/// the model is safe to read.
/// <para>
/// Coordinates: a WinUI 3 text provider works in device-independent pixels from the top left of the window's content, not in screen pixels (S7,
/// surprise 1); <c>RangeFromPoint</c> is given the same.
/// </para>
/// <para>Not implemented: text attributes (font, colour), annotations as children of the text, the "this document is not tagged" announcement, and the structure tree for tagged files. Narrator, NVDA and Accessibility Insights have not been run against this.</para>
/// </summary>
public sealed class PageSurfaceAutomationPeer : FrameworkElementAutomationPeer, ITextProvider, ITextProvider2
{
    private readonly PageSurface _surface;

    public PageSurfaceAutomationPeer(PageSurface owner)
        : base(owner)
    {
        _surface = owner;
    }

    protected override string GetClassNameCore() => nameof(PageSurface);

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Document;

    protected override bool IsControlElementCore() => true;

    protected override bool IsContentElementCore() => true;

    protected override bool IsKeyboardFocusableCore() => true;

    // The drawing element and the overlay are not offered: the text is.
    protected override IList<AutomationPeer> GetChildrenCore() => [];

    protected override object GetPatternCore(PatternInterface patternInterface) => patternInterface switch
    {
        PatternInterface.Text or PatternInterface.Text2 => this,
        _ => base.GetPatternCore(patternInterface),
    };

    internal void RaiseTextChanged()
    {
        if (ListenerExists(AutomationEvents.TextPatternOnTextChanged))
        {
            RaiseAutomationEvent(AutomationEvents.TextPatternOnTextChanged);
        }
    }

    internal void RaiseSelectionChanged()
    {
        if (ListenerExists(AutomationEvents.TextPatternOnTextSelectionChanged))
        {
            RaiseAutomationEvent(AutomationEvents.TextPatternOnTextSelectionChanged);
        }
    }

    internal IRawElementProviderSimple Self() => ProviderFromPeer(this);

    // ---- ITextProvider -------------------------------------------------------------------------------------------------------------

    private TextModel Model => _surface.AccessibleModel;

    public ITextRangeProvider DocumentRange => new PageTextRangeProvider(this, _surface, new TextRange(Model, 0, Model.Length));

    public SupportedTextSelection SupportedTextSelection => SupportedTextSelection.Single;

    public ITextRangeProvider[] GetSelection()
    {
        // A degenerate range (a caret) when nothing is selected, as the Win32 edit control reports.
        var (start, end) = _surface.AccessibleSelection();
        return [new PageTextRangeProvider(this, _surface, new TextRange(Model, start, end))];
    }

    public ITextRangeProvider[] GetVisibleRanges()
    {
        var (start, end) = _surface.AccessibleVisibleAtoms();
        return [new PageTextRangeProvider(this, _surface, new TextRange(Model, start, end))];
    }

    public ITextRangeProvider RangeFromChild(IRawElementProviderSimple childElement) => DocumentRange;

    public ITextRangeProvider RangeFromPoint(Point screenLocation)
    {
        var atom = _surface.AccessibleAtomAt(screenLocation);
        var at = Math.Max(0, atom);
        return new PageTextRangeProvider(this, _surface, new TextRange(Model, at, at));
    }

    // ---- ITextProvider2 --------------------------------------------------------------------------------------------------------------

    public ITextRangeProvider GetCaretRange(out bool isActive)
    {
        isActive = _surface.HasFocusForAccessibility;
        var (start, _) = _surface.AccessibleSelection();
        return new PageTextRangeProvider(this, _surface, new TextRange(Model, start, start));
    }

    public ITextRangeProvider RangeFromAnnotation(IRawElementProviderSimple annotationElement) => DocumentRange;
}

/// <summary>A UI Automation text range over the combined model: a thin adapter, the logic is <see cref="TextRange"/>.</summary>
public sealed class PageTextRangeProvider : ITextRangeProvider
{
    private readonly PageSurfaceAutomationPeer _peer;
    private readonly PageSurface _surface;
    private readonly TextRange _range;

    internal PageTextRangeProvider(PageSurfaceAutomationPeer peer, PageSurface surface, TextRange range)
    {
        _peer = peer;
        _surface = surface;
        _range = range;
    }

    private static TextUnitKind Unit(TextUnit unit) => unit switch
    {
        TextUnit.Character => TextUnitKind.Character,
        TextUnit.Format => TextUnitKind.Format,
        TextUnit.Word => TextUnitKind.Word,
        TextUnit.Line => TextUnitKind.Line,
        TextUnit.Paragraph => TextUnitKind.Paragraph,
        TextUnit.Page => TextUnitKind.Page,
        _ => TextUnitKind.Document,
    };

    private static RangeEndpoint Endpoint(TextPatternRangeEndpoint endpoint) => endpoint == TextPatternRangeEndpoint.Start ? RangeEndpoint.Start : RangeEndpoint.End;

    private static TextRange Unwrap(ITextRangeProvider provider) =>
        provider is PageTextRangeProvider ours ? ours._range : throw new ArgumentException("A range from another provider.", nameof(provider));

    public ITextRangeProvider Clone() => new PageTextRangeProvider(_peer, _surface, _range.Clone());

    public bool Compare(ITextRangeProvider textRangeProvider) => _range.Compare(Unwrap(textRangeProvider));

    public int CompareEndpoints(TextPatternRangeEndpoint endpoint, ITextRangeProvider textRangeProvider, TextPatternRangeEndpoint targetEndpoint) =>
        _range.CompareEndpoints(Endpoint(endpoint), Unwrap(textRangeProvider), Endpoint(targetEndpoint));

    public void ExpandToEnclosingUnit(TextUnit unit) => _range.ExpandToEnclosingUnit(Unit(unit));

    public ITextRangeProvider? FindAttribute(int attributeId, object value, bool backward) => null;

    public ITextRangeProvider? FindText(string text, bool backward, bool ignoreCase)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var model = _range.Model;
        var from = model.OffsetOfAtom(_range.Start);
        var span = model.Text.AsSpan(from, model.OffsetOfAtom(_range.End) - from);
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var index = backward ? span.LastIndexOf(text, comparison) : span.IndexOf(text, comparison);
        if (index < 0)
        {
            return null;
        }

        var start = model.AtomAtOffset(from + index);
        var end = model.AtomAtOffset(from + index + text.Length);
        return new PageTextRangeProvider(_peer, _surface, new TextRange(model, start, Math.Max(end, start + 1)));
    }

    public object GetAttributeValue(int attributeId) => DependencyProperty.UnsetValue;

    public void GetBoundingRectangles(out double[] returnValue)
    {
        var values = new List<double>();
        foreach (var rect in _surface.AccessibleBounds(_range.Start, _range.End))
        {
            values.Add(rect.X);
            values.Add(rect.Y);
            values.Add(rect.Width);
            values.Add(rect.Height);
        }

        returnValue = [.. values];
    }

    public IRawElementProviderSimple[] GetChildren() => [];

    public IRawElementProviderSimple GetEnclosingElement() => _peer.Self();

    public string GetText(int maxLength) => _range.GetText(maxLength);

    public int Move(TextUnit unit, int count) => _range.Move(Unit(unit), count);

    public void MoveEndpointByRange(TextPatternRangeEndpoint endpoint, ITextRangeProvider textRangeProvider, TextPatternRangeEndpoint targetEndpoint) =>
        _range.MoveEndpointByRange(Endpoint(endpoint), Unwrap(textRangeProvider), Endpoint(targetEndpoint));

    public int MoveEndpointByUnit(TextPatternRangeEndpoint endpoint, TextUnit unit, int count) =>
        _range.MoveEndpointByUnit(Endpoint(endpoint), Unit(unit), count);

    public void ScrollIntoView(bool alignToTop) => _surface.RevealAccessibleRange(_range.Start, _range.End);

    public void Select() => _surface.SelectAccessibleRange(_range.Start, _range.End);

    public void AddToSelection() => throw new NotSupportedException("The page surface supports a single selection.");

    public void RemoveFromSelection() => throw new NotSupportedException("The page surface supports a single selection.");
}
