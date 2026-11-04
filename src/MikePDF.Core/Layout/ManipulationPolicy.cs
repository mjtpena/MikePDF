namespace MikePDF.Core.Layout;

/// <summary>The device a pointer gesture comes from (the Core's own copy of the WinUI enumeration, so the policy can be tested without WinUI).</summary>
public enum PointerKind
{
    Touch,
    Pen,
    Mouse,
    Touchpad,
}

/// <summary>
/// Who may move the view with a manipulation (a drag, a pinch, and the inertia that follows). WinUI raises manipulations for a mouse or a pen drag as well as for
/// touch once <c>ManipulationMode</c> is not <c>System</c>, so without this rule a drag that selects text also pans the document and, in the paged flow, can turn the page.
/// </summary>
public static class ManipulationPolicy
{
    /// <summary>
    /// Whether a manipulation by <paramref name="device"/> may pan, zoom, run inertia or turn a page. A finger always may. A mouse or touchpad never may: it scrolls with the wheel, the
    /// scrollbars and the auto-scroll of a selection drag, and its drag selects. A pen may, except when its press began on the page (<paramref name="selectionPressActive"/>: a
    /// selection or a link), when its drag selects too.
    /// </summary>
    public static bool MayMoveView(PointerKind device, bool selectionPressActive) => device switch
    {
        PointerKind.Touch => true,
        PointerKind.Pen => !selectionPressActive,
        _ => false,
    };
}
