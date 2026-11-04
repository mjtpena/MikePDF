namespace MikePDF.Markup;

/// <summary>Where a note's rectangle goes in the file (sticky-notes 2.2, 2.4). The placement from a click needs the page's geometry and is in <c>MikePDF.Markup.Editing</c>.</summary>
public static class NoteGeometry
{
    public const double PopupWidth = 180;
    public const double PopupHeight = 100;

    /// <summary>
    /// The <c>/Rect</c> to write for an icon displayed in <paramref name="shown"/> (user space) on a page turned <paramref name="pageRotation"/> degrees. The icon
    /// is NoRotate, and a reader keeps its upper-left corner of the rectangle, in the page's own unturned space, where it is and draws the icon upright from
    /// there (ISO 32000-1 12.5.3; measured against PDFium for OQ-SN-3). So the rectangle hangs down and to the right of the user-space point where the displayed
    /// top-left corner is.
    /// </summary>
    public static PdfRect SavedRect(PdfRect shown, int pageRotation)
    {
        var size = NoteArtwork.IconSize;
        var (ax, ay) = (((pageRotation % 360) + 360) % 360) switch
        {
            90 => (shown.Left, shown.Bottom),
            180 => (shown.Right, shown.Bottom),
            270 => (shown.Right, shown.Top),
            _ => (shown.Left, shown.Top),
        };
        return pageRotation % 360 == 0 ? shown : new PdfRect(ax, ay - size, ax + size, ay);
    }

    /// <summary>The inverse of <see cref="SavedRect"/>: the displayed icon of a note read back from a file written by it.</summary>
    public static PdfRect FromSavedRect(PdfRect saved, int pageRotation)
    {
        var size = NoteArtwork.IconSize;
        var (ax, ay) = (saved.Left, saved.Top);
        return (((pageRotation % 360) + 360) % 360) switch
        {
            90 => new PdfRect(ax, ay, ax + size, ay + size),
            180 => new PdfRect(ax - size, ay, ax, ay + size),
            270 => new PdfRect(ax - size, ay - size, ax, ay),
            _ => saved,
        };
    }
}
