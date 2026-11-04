using CoreGeo = MikePDF.Core.Geometry;

namespace MikePDF.Markup.Editing;

/// <summary>
/// Where an annotation tool meets the page (master prompt 6.3): converts between the displayed page (rotation applied, origin top-left, y down) and PDF
/// user space (origin bottom-left, MediaBox and CropBox offsets, no rotation), which is where every annotation is stored. Wraps
/// <see cref="CoreGeo.PageGeometry"/>, so the conversions are the ones the viewer already uses and tests; the values in and out are the Markup library's.
/// </summary>
public sealed class PageFrame(CoreGeo.PageGeometry geometry)
{
    public CoreGeo.PageGeometry Geometry { get; } = geometry ?? throw new ArgumentNullException(nameof(geometry));

    /// <summary>The page's own /Rotate in degrees.</summary>
    public int RotateDegrees => Geometry.RotateTurns * 90;

    /// <summary>The page as displayed, in points.</summary>
    public SizeD DisplaySize => new(Geometry.Size.Width, Geometry.Size.Height);

    /// <summary>The visible box in user space: the CropBox clipped to the MediaBox. Creation clamps to it (annotation-core 3.4).</summary>
    public PdfRect Box => new(Geometry.Box.Left, Geometry.Box.Bottom, Geometry.Box.Right, Geometry.Box.Top);

    public PointD ToUser(PointD display)
    {
        var p = Geometry.PageToPdf(new CoreGeo.PointD(display.X, display.Y));
        return new PointD(p.X, p.Y);
    }

    public PointD ToDisplay(PointD user)
    {
        var p = Geometry.PdfToPage(new CoreGeo.PointD(user.X, user.Y));
        return new PointD(p.X, p.Y);
    }

    /// <summary>The user-space rectangle that covers a rectangle of the displayed page.</summary>
    public PdfRect ToUser(RectD display)
    {
        var r = Geometry.PageToPdf(new CoreGeo.RectD(display.X, display.Y, display.Width, display.Height));
        return new PdfRect(r.Left, r.Bottom, r.Right, r.Top);
    }

    /// <summary>The displayed rectangle that covers a user-space rectangle.</summary>
    public RectD ToDisplay(PdfRect user)
    {
        var r = Geometry.PdfToPage(new CoreGeo.PdfRect(user.Left, user.Bottom, user.Right, user.Top));
        return new RectD(r.X, r.Y, r.Width, r.Height);
    }

    /// <summary>A displayed point pulled inside the displayed page.</summary>
    public PointD ClampDisplay(PointD point) => new(Math.Clamp(point.X, 0, DisplaySize.Width), Math.Clamp(point.Y, 0, DisplaySize.Height));

    /// <summary>A user-space point pulled inside the visible box.</summary>
    public PointD ClampUser(PointD point) => new(Math.Clamp(point.X, Box.Left, Box.Right), Math.Clamp(point.Y, Box.Bottom, Box.Top));
}

/// <summary>Where notes and their popups go from a click (sticky-notes 2.2, 2.4). The rule for the rectangle the file holds is <see cref="NoteGeometry"/>.</summary>
public static class NotePlacement
{
    /// <summary>
    /// The 20 x 20 rectangle of an icon whose top-left corner (as displayed) is at a displayed point, in user space. It is kept inside the displayed page,
    /// and far enough in that the rectangle the file will hold (<see cref="NoteGeometry.SavedRect"/>) is inside the page too.
    /// </summary>
    public static PdfRect Place(PageFrame frame, PointD displayTopLeft)
    {
        var size = NoteArtwork.IconSize;
        var x = Math.Clamp(displayTopLeft.X, 0, Math.Max(0, frame.DisplaySize.Width - size));
        var y = Math.Clamp(displayTopLeft.Y, 0, Math.Max(0, frame.DisplaySize.Height - size));
        var shown = frame.ToUser(new RectD(x, y, size, size));
        var saved = NoteGeometry.SavedRect(shown, frame.RotateDegrees);
        var (sx, sy) = (saved.Left - shown.Left, saved.Bottom - shown.Bottom);
        var box = frame.Box;
        var left = Math.Clamp(shown.Left, box.Left + Math.Max(0, -sx), Math.Max(box.Left, box.Right - size - Math.Max(0, sx)));
        var bottom = Math.Clamp(shown.Bottom, box.Bottom + Math.Max(0, -sy), Math.Max(box.Bottom, box.Top - size - Math.Max(0, sy)));
        return new PdfRect(left, bottom, left + size, bottom + size);
    }

    /// <summary>The popup rectangle: 180 x 100 points to the right of the icon as displayed, moved to stay inside the displayed page (SN-05).</summary>
    public static PdfRect PopupFor(PageFrame frame, PdfRect icon)
    {
        var shown = frame.ToDisplay(icon);
        var page = frame.DisplaySize;
        var x = Math.Min(shown.Right + 4, Math.Max(0, page.Width - NoteGeometry.PopupWidth));
        var y = Math.Min(shown.Y, Math.Max(0, page.Height - NoteGeometry.PopupHeight));
        return frame.ToUser(new RectD(Math.Max(0, x), Math.Max(0, y), Math.Min(NoteGeometry.PopupWidth, page.Width), Math.Min(NoteGeometry.PopupHeight, page.Height)));
    }
}

/// <summary>The tools' way of making annotations: from points of the displayed page, through the page's geometry (the Markup library's factory works in user space).</summary>
public static class FrameFactory
{
    /// <summary>A highlight, underline, strike-out or squiggly with its comment's popup placed by <paramref name="frame"/>.</summary>
    public static Annotation TextMarkup(this MarkupFactory factory, MarkupKind kind, int page, IReadOnlyList<Quad> quads, PageFrame frame, Rgb? color = null, double opacity = 1, double thickness = 1, string contents = "")
    {
        ArgumentNullException.ThrowIfNull(factory);
        var bare = factory.TextMarkup(kind, page, quads, color, opacity, thickness, contents);
        return bare.Contents.Length > 0 ? bare with { PopupRect = NotePlacement.PopupFor(frame, bare.Rect) } : bare;
    }

    /// <summary>A note with its icon's top-left corner at a displayed point (sticky-notes 2.2); its popup is placed beside it.</summary>
    public static Annotation Note(this MarkupFactory factory, int page, PageFrame frame, PointD displayTopLeft, string contents, NoteIcon icon = NoteIcon.Comment, Rgb? color = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var rect = NotePlacement.Place(frame, displayTopLeft);
        return factory.NoteAt(page, rect, NotePlacement.PopupFor(frame, rect), frame.RotateDegrees, contents, icon, color);
    }

    /// <summary>
    /// A text box over a rectangle of the displayed page (a click or a drag with the tool). The rectangle is turned into user space through the page's
    /// geometry, and the box remembers the page's rotation so that it can grow the right way.
    /// </summary>
    public static Annotation FreeTextOnPage(this MarkupFactory factory, int page, PageFrame frame, RectD displayRect, string text, FontFamily family = FontFamily.Sans, bool bold = false,
        bool italic = false, double size = 12, Rgb? textColor = null, TextAlign align = TextAlign.Left, bool typewriter = false, double borderWidth = 0, Rgb? borderColor = null, Rgb? fill = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return factory.FreeText(page, frame.ToUser(displayRect), text, family, bold, italic, size, textColor, align, typewriter, borderWidth, borderColor, fill, frame.RotateDegrees);
    }

    /// <summary>
    /// A built-in stamp placed on the displayed page: fitted into 200 x 100 points (or into the dragged box) as it is displayed, with the user's quarter
    /// turns, then turned into user space through the page's geometry (stamps 3.4, 3.5).
    /// </summary>
    public static Annotation StampOnPage(this MarkupFactory factory, int page, PageFrame frame, string name, PointD displayTopLeft, RectD? displayDrag = null, int userTurns = 0, double opacity = 1)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var fit = MarkupFactory.StampSizeFor(name, userTurns, displayDrag?.Width ?? 200, displayDrag?.Height ?? 100);
        var origin = displayDrag is { } drag ? new PointD(drag.X, drag.Y) : displayTopLeft;
        return factory.StampAt(page, name, frame.ToUser(new RectD(origin.X, origin.Y, fit.Width, fit.Height)), userTurns, opacity);
    }
}
