namespace MikePDF.Core.Geometry;

/// <summary>A point. The meaning of X and Y (PDF user space, page space, device pixels) is given by the API that returns it.</summary>
public readonly record struct PointD(double X, double Y);

public readonly record struct SizeD(double Width, double Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>A rectangle with the origin at the top-left (page, view and device space).</summary>
public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Intersects(RectD other) => X < other.Right && other.X < Right && Y < other.Bottom && other.Y < Bottom;

    public RectD Intersect(RectD other)
    {
        var left = Math.Max(X, other.X);
        var top = Math.Max(Y, other.Y);
        var right = Math.Min(Right, other.Right);
        var bottom = Math.Min(Bottom, other.Bottom);
        return right <= left || bottom <= top ? new RectD(left, top, 0, 0) : new RectD(left, top, right - left, bottom - top);
    }

    /// <summary>The smallest rectangle that holds both.</summary>
    public RectD Union(RectD other)
    {
        var left = Math.Min(X, other.X);
        var top = Math.Min(Y, other.Y);
        return new RectD(left, top, Math.Max(Right, other.Right) - left, Math.Max(Bottom, other.Bottom) - top);
    }

    public bool Contains(PointD point) => point.X >= X && point.X < Right && point.Y >= Y && point.Y < Bottom;

    /// <summary>Zero when the point is inside; otherwise the straight-line distance to the nearest edge.</summary>
    public double DistanceTo(PointD point)
    {
        var dx = point.X < X ? X - point.X : point.X > Right ? point.X - Right : 0;
        var dy = point.Y < Y ? Y - point.Y : point.Y > Bottom ? point.Y - Bottom : 0;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    public RectD Offset(double dx, double dy) => new(X + dx, Y + dy, Width, Height);

    public RectD Inflate(double amount) => new(X - amount, Y - amount, Width + (2 * amount), Height + (2 * amount));
}

/// <summary>A rectangle in PDF user space: points, origin bottom-left, y up. Always normalised (Left &lt;= Right, Bottom &lt;= Top).</summary>
public readonly record struct PdfRect(double Left, double Bottom, double Right, double Top)
{
    public double Width => Right - Left;

    public double Height => Top - Bottom;

    public static PdfRect FromCorners(double x1, double y1, double x2, double y2) =>
        new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));

    public PdfRect Intersect(PdfRect other) => new(Math.Max(Left, other.Left), Math.Max(Bottom, other.Bottom), Math.Min(Right, other.Right), Math.Min(Top, other.Top));

    public bool IsEmpty => Right <= Left || Top <= Bottom;
}

/// <summary>Quarter turns clockwise, 0 to 3.</summary>
public static class QuarterTurns
{
    public static int Normalize(int turns) => ((turns % 4) + 4) % 4;

    /// <summary>A /Rotate value in degrees (a multiple of 90, possibly negative or above 360) to quarter turns.</summary>
    public static int FromDegrees(int degrees) => Normalize(degrees / 90);

    public static bool IsOdd(int turns) => (Normalize(turns) & 1) != 0;

    /// <summary>The size of a rectangle after it was turned clockwise by <paramref name="turns"/> quarter turns.</summary>
    public static SizeD Rotate(SizeD size, int turns) => IsOdd(turns) ? new SizeD(size.Height, size.Width) : size;

    /// <summary>
    /// Where a point of a rectangle of size <paramref name="before"/> (origin top-left, y down) ends up when the rectangle is turned clockwise
    /// by <paramref name="turns"/> quarter turns. The result is in the turned rectangle, again with its origin at the top-left.
    /// </summary>
    public static PointD Rotate(PointD p, SizeD before, int turns) => Normalize(turns) switch
    {
        1 => new PointD(before.Height - p.Y, p.X),
        2 => new PointD(before.Width - p.X, before.Height - p.Y),
        3 => new PointD(p.Y, before.Width - p.X),
        _ => p,
    };

    /// <summary>The inverse of <see cref="Rotate(PointD, SizeD, int)"/>: <paramref name="after"/> is the size of the turned rectangle.</summary>
    public static PointD RotateBack(PointD p, SizeD after, int turns) => Rotate(p, after, 4 - Normalize(turns));
}
