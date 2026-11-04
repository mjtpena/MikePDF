
namespace MikePDF.Markup.Editing;

/// <summary>
/// The arithmetic of drawing a shape with the pointer (shapes 3.3, SH-03): a drag that is too short makes nothing, Shift constrains, Alt draws from the centre.
/// All in one space: the caller works in the displayed page, so that Shift means a multiple of 45 degrees on the screen, and converts the result to user
/// space with the page's frame.
/// </summary>
public static class ShapeGestures
{
    /// <summary>A drag shorter than this, in points, is an accident and creates nothing.</summary>
    public const double MinDrag = 4;

    /// <summary>A click this close to the first vertex (DIP) closes a polygon.</summary>
    public const double CloseDip = 8;

    /// <summary>The rectangle (or ellipse box) of a drag; null when the drag is too short. Shift makes it square, Alt makes the start point its centre.</summary>
    public static PdfRect? DragRect(PointD start, PointD current, bool shift = false, bool alt = false)
    {
        var (dx, dy) = (current.X - start.X, current.Y - start.Y);
        if (Math.Sqrt((dx * dx) + (dy * dy)) < MinDrag)
        {
            return null;
        }

        if (shift)
        {
            var side = Math.Max(Math.Abs(dx), Math.Abs(dy));
            (dx, dy) = (Math.Sign(dx == 0 ? 1 : dx) * side, Math.Sign(dy == 0 ? 1 : dy) * side);
        }

        return alt
            ? PdfRect.FromCorners(start.X - dx, start.Y - dy, start.X + dx, start.Y + dy)
            : PdfRect.FromCorners(start.X, start.Y, start.X + dx, start.Y + dy);
    }

    /// <summary>The two ends of a line or arrow dragged out; null when too short. Shift snaps the angle to a multiple of 45 degrees, Alt makes the start the middle.</summary>
    public static (PointD Start, PointD End)? DragLine(PointD start, PointD current, bool shift = false, bool alt = false)
    {
        var (dx, dy) = (current.X - start.X, current.Y - start.Y);
        var length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length < MinDrag)
        {
            return null;
        }

        if (shift)
        {
            var angle = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4);
            (dx, dy) = (length * Math.Cos(angle), length * Math.Sin(angle));
        }

        var end = new PointD(start.X + dx, start.Y + dy);
        return alt ? (new PointD(start.X - dx, start.Y - dy), end) : (start, end);
    }
}

/// <summary>
/// A polygon or polyline being drawn (shapes 3.3): click adds a vertex, Backspace removes the last, Enter or a double click completes, a click next to the
/// first vertex closes a polygon. Not on the undo stack until it is completed (shapes 8).
/// </summary>
public sealed class PolygonBuilder(bool closed)
{
    private readonly List<PointD> _vertices = [];

    public bool IsPolygon { get; } = closed;

    public IReadOnlyList<PointD> Vertices => _vertices;

    public int Minimum => IsPolygon ? ShapeGeometry.MinPolygonVertices : ShapeGeometry.MinPolylineVertices;

    /// <summary>Adds a vertex. False (and nothing added) at the limit of 1,000, where the tool says so and ignores clicks until completion.</summary>
    public bool Add(PointD point)
    {
        if (_vertices.Count >= ShapeGeometry.MaxVertices)
        {
            return false;
        }

        _vertices.Add(point);
        return true;
    }

    public bool Backspace()
    {
        if (_vertices.Count == 0)
        {
            return false;
        }

        _vertices.RemoveAt(_vertices.Count - 1);
        return true;
    }

    /// <summary>Whether a click at <paramref name="point"/> closes the polygon: it has three vertices and the click is within <paramref name="tolerance"/> of the first.</summary>
    public bool ClosesAt(PointD point, double tolerance) =>
        IsPolygon && _vertices.Count >= ShapeGeometry.MinPolygonVertices && Geo.Distance(_vertices[0], point) <= tolerance;

    public bool CanComplete => MarkupFactory.Dedupe(_vertices, IsPolygon).Count >= Minimum;

    /// <summary>The vertices of the finished shape with duplicates merged, or null when there are too few (the shape stays in progress, with a message).</summary>
    public IReadOnlyList<PointD>? Complete() => CanComplete ? MarkupFactory.Dedupe(_vertices, IsPolygon) : null;

    /// <summary>The message key for a refused completion (<c>Shape_NeedPolygon</c> or <c>Shape_NeedPolyline</c>).</summary>
    public string NeedKey => IsPolygon ? "Shape_NeedPolygon" : "Shape_NeedPolyline";
}
