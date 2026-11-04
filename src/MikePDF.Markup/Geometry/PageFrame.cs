using MikePDF.Core.Geometry;

namespace MikePDF.Markup;

/// <summary>
/// Where an annotation tool meets the page (master prompt 6.3): converts between the displayed page (rotation applied, origin top-left, y down) and PDF
/// user space (origin bottom-left, MediaBox and CropBox offsets, no rotation), which is where every annotation is stored. Wraps
/// <see cref="PageGeometry"/>, so the conversions are the ones the viewer already uses and tests.
/// </summary>
public sealed class PageFrame(PageGeometry geometry)
{
    public PageGeometry Geometry { get; } = geometry ?? throw new ArgumentNullException(nameof(geometry));

    /// <summary>The page's own /Rotate in degrees.</summary>
    public int RotateDegrees => Geometry.RotateTurns * 90;

    /// <summary>The page as displayed, in points.</summary>
    public SizeD DisplaySize => Geometry.Size;

    /// <summary>The visible box in user space: the CropBox clipped to the MediaBox. Creation clamps to it (annotation-core 3.4).</summary>
    public PdfRect Box => Geometry.Box;

    public PointD ToUser(PointD display) => Geometry.PageToPdf(display);

    public PointD ToDisplay(PointD user) => Geometry.PdfToPage(user);

    /// <summary>The user-space rectangle that covers a rectangle of the displayed page.</summary>
    public PdfRect ToUser(RectD display) => Geometry.PageToPdf(display);

    /// <summary>The displayed rectangle that covers a user-space rectangle.</summary>
    public RectD ToDisplay(PdfRect user) => Geometry.PdfToPage(user);

    /// <summary>A displayed point pulled inside the displayed page.</summary>
    public PointD ClampDisplay(PointD point) => new(Math.Clamp(point.X, 0, DisplaySize.Width), Math.Clamp(point.Y, 0, DisplaySize.Height));

    /// <summary>A user-space point pulled inside the visible box.</summary>
    public PointD ClampUser(PointD point) => new(Math.Clamp(point.X, Box.Left, Box.Right), Math.Clamp(point.Y, Box.Bottom, Box.Top));
}

/// <summary>Where notes and their popups go (sticky-notes 2.2, 2.4).</summary>
public static class NoteGeometry
{
    public const double PopupWidth = 180;
    public const double PopupHeight = 100;

    /// <summary>The 20 x 20 rectangle of an icon whose top-left corner (as displayed) is at a displayed point, kept inside the displayed page, in user space.</summary>
    public static PdfRect Place(PageFrame frame, PointD displayTopLeft)
    {
        var size = NoteArtwork.IconSize;
        var x = Math.Clamp(displayTopLeft.X, 0, Math.Max(0, frame.DisplaySize.Width - size));
        var y = Math.Clamp(displayTopLeft.Y, 0, Math.Max(0, frame.DisplaySize.Height - size));
        return frame.ToUser(new RectD(x, y, size, size));
    }

    /// <summary>The popup rectangle: 180 x 100 points to the right of the icon as displayed, moved to stay inside the displayed page (SN-05).</summary>
    public static PdfRect PopupFor(PageFrame frame, PdfRect icon)
    {
        var shown = frame.ToDisplay(icon);
        var page = frame.DisplaySize;
        var x = Math.Min(shown.Right + 4, Math.Max(0, page.Width - PopupWidth));
        var y = Math.Min(shown.Y, Math.Max(0, page.Height - PopupHeight));
        return frame.ToUser(new RectD(Math.Max(0, x), Math.Max(0, y), Math.Min(PopupWidth, page.Width), Math.Min(PopupHeight, page.Height)));
    }
}
