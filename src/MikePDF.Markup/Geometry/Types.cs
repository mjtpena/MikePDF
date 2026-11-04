namespace MikePDF.Markup;

/// <summary>A point in points. In PDF user space unless the member that takes it says it is displayed.</summary>
public readonly record struct PointD(double X, double Y);

/// <summary>A size in points.</summary>
public readonly record struct SizeD(double Width, double Height);

/// <summary>A rectangle of the displayed page: origin at the top-left, y down (master prompt 6.3 "page space").</summary>
public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;
}

/// <summary>A rectangle in PDF user space: origin at the bottom-left, as <c>/Rect</c> is written. Normalised when left is below right and bottom below top.</summary>
public readonly record struct PdfRect(double Left, double Bottom, double Right, double Top)
{
    public double Width => Right - Left;

    public double Height => Top - Bottom;

    public bool IsEmpty => Right <= Left || Top <= Bottom;

    public static PdfRect FromCorners(double x1, double y1, double x2, double y2) =>
        new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));
}
