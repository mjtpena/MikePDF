
namespace MikePDF.Markup.Editing;

/// <summary>A uniform grid over the annotations of one page, so that finding what is under the pointer does not scan them all (annotation-core 7, AC-04).</summary>
internal sealed class SpatialIndex
{
    private const double CellSize = 64;

    /// <summary>The most a query's tolerance may reach beyond an annotation's rectangle; larger tolerances fall back to a scan.</summary>
    public const double Reach = 32;

    private readonly Dictionary<(int, int), List<int>> _cells = [];
    private readonly IReadOnlyList<Annotation> _items;
    private readonly List<int> _unindexed = [];

    public SpatialIndex(IReadOnlyList<Annotation> items)
    {
        _items = items;
        for (var i = 0; i < items.Count; i++)
        {
            var r = Geo.Inflate(items[i].Rect, Reach + (items[i].Width / 2));
            if (!Geo.IsFinite(r) || (r.Right - r.Left) / CellSize * ((r.Top - r.Bottom) / CellSize) > 4096)
            {
                _unindexed.Add(i);
                continue;
            }

            for (var x = Cell(r.Left); x <= Cell(r.Right); x++)
            {
                for (var y = Cell(r.Bottom); y <= Cell(r.Top); y++)
                {
                    if (!_cells.TryGetValue((x, y), out var list))
                    {
                        _cells[(x, y)] = list = [];
                    }

                    list.Add(i);
                }
            }
        }
    }

    /// <summary>The indexes of the annotations whose box reaches the point, ascending.</summary>
    public IEnumerable<int> Candidates(PointD p)
    {
        var found = new SortedSet<int>(_unindexed);
        if (_cells.TryGetValue((Cell(p.X), Cell(p.Y)), out var list))
        {
            foreach (var i in list)
            {
                found.Add(i);
            }
        }

        return found;
    }

    private static int Cell(double v) => (int)Math.Floor(Math.Clamp(v, -1e8, 1e8) / CellSize);
}

/// <summary>What a hit found: the annotation and its place in the page's order.</summary>
public sealed record Hit(Annotation Annotation, int Index);

/// <summary>
/// Which annotation is under a point (annotation-core 3.4, shapes 3.4, AC-04): the topmost wins, later in the page's order is on top; filled shapes
/// hit anywhere inside and unfilled ones within the tolerance of their stroke; text markup hits on its quads; ink within its stroke's width. The
/// tolerance is in points of the page (the caller converts 6 DIP for a mouse and 12 DIP for touch and pen, at the zoom, annotation-core 3.4).
/// </summary>
public static class HitTester
{
    /// <summary>Tolerance for a mouse, in DIP (annotation-core 3.4, proposed).</summary>
    public const double MouseToleranceDip = 6;

    /// <summary>Tolerance for touch and pen, in DIP (annotation-core 3.4, proposed).</summary>
    public const double TouchToleranceDip = 12;

    public static double ToPoints(double dip, double zoomPixelsPerPoint) => dip / Math.Max(zoomPixelsPerPoint, 1e-6);

    /// <summary>The topmost annotation under <paramref name="point"/> on a page, or null.</summary>
    public static Hit? HitTest(AnnotationStore store, int page, PointD point, double tolerance)
    {
        ArgumentNullException.ThrowIfNull(store);
        var list = store.OnPage(page);
        if (list.Count == 0)
        {
            return null;
        }

        var candidates = tolerance <= SpatialIndex.Reach ? store.IndexFor(page).Candidates(point).Reverse() : Enumerable.Range(0, list.Count).Reverse();
        foreach (var i in candidates)
        {
            if (!list[i].IsHiddenFromHits() && Hits(list[i], point, tolerance))
            {
                return new Hit(list[i], i);
            }
        }

        return null;
    }

    /// <summary>Every annotation under a point, topmost first (for cycling through overlapping notes).</summary>
    public static IReadOnlyList<Hit> HitAll(AnnotationStore store, int page, PointD point, double tolerance)
    {
        var list = store.OnPage(page);
        return [.. Enumerable.Range(0, list.Count).Reverse().Where(i => !list[i].IsHiddenFromHits() && Hits(list[i], point, tolerance)).Select(i => new Hit(list[i], i))];
    }

    /// <summary>The annotations of a page whose geometry meets a rectangle (rubber-band selection).</summary>
    public static IReadOnlyList<Hit> HitRectangle(AnnotationStore store, int page, PdfRect area)
    {
        var list = store.OnPage(page);
        return [.. Enumerable.Range(0, list.Count).Where(i => !list[i].IsHiddenFromHits() && Geo.Intersects(list[i].Rect, area)).Select(i => new Hit(list[i], i))];
    }

    public static bool Hits(Annotation a, PointD p, double tolerance)
    {
        if (!Geo.Contains(Geo.Inflate(a.Rect, tolerance + (a.Width / 2)), p))
        {
            return false;
        }

        switch (a.Payload)
        {
            case TextMarkupPayload markup:
                return markup.Quads.Any(q => InsideQuad(q, p) || DistanceToPolygon(q.Outline(), p) <= tolerance);
            case InkPayload ink:
                return ink.Strokes.Any(s => NearStroke(s, p, tolerance));
            case LinePayload line:
                return Geo.DistanceToSegment(p, line.Start, line.End) <= tolerance + (a.Width / 2);
            case PolyPayload poly:
            {
                var points = poly.Vertices;
                var closed = a.Kind == MarkupKind.Polygon;
                if (closed && a.Interior is not null && Geo.InsidePolygonNonZero(p, points))
                {
                    return true;
                }

                return DistanceToOutline(points, closed, p) <= tolerance + (a.Width / 2);
            }

            case BoxPayload when a.Kind == MarkupKind.Ellipse:
            {
                var ring = EllipseRing(a.Rect);
                if (a.Interior is not null && Geo.InsidePolygonNonZero(p, ring))
                {
                    return true;
                }

                return DistanceToOutline(ring, true, p) <= tolerance + (a.Width / 2);
            }

            case BoxPayload:
            {
                if (a.Interior is not null && Geo.Contains(Geo.Inflate(a.Rect, tolerance), p))
                {
                    return true;
                }

                var r = a.Rect;
                return DistanceToOutline([new PointD(r.Left, r.Bottom), new PointD(r.Right, r.Bottom), new PointD(r.Right, r.Top), new PointD(r.Left, r.Top)], true, p) <= tolerance + (a.Width / 2);
            }

            default:
                // Notes, text boxes and stamps hit anywhere in their rectangle.
                return Geo.Contains(Geo.Inflate(a.Rect, tolerance), p);
        }
    }

    private static bool IsHiddenFromHits(this Annotation a) => a.ReplyKind != ReplyKind.None;

    private static bool InsideQuad(Quad q, PointD p) => Geo.InsidePolygonNonZero(p, q.Outline());

    private static double DistanceToPolygon(IReadOnlyList<PointD> polygon, PointD p) => DistanceToOutline(polygon, true, p);

    private static double DistanceToOutline(IReadOnlyList<PointD> points, bool closed, PointD p)
    {
        var best = double.MaxValue;
        var count = closed ? points.Count : points.Count - 1;
        for (var i = 0; i < count; i++)
        {
            best = Math.Min(best, Geo.DistanceToSegment(p, points[i], points[(i + 1) % points.Count]));
        }

        return points.Count == 1 ? Geo.Distance(p, points[0]) : best;
    }

    private static bool NearStroke(InkStrokeData s, PointD p, double tolerance)
    {
        var n = s.PointCount;
        if (n == 0)
        {
            return false;
        }

        if (n == 1)
        {
            return Geo.Distance(p, new PointD(s.Xy[0], s.Xy[1])) <= tolerance + (s.Widths[0] / 2);
        }

        for (var i = 0; i + 1 < n; i++)
        {
            var reach = tolerance + (Math.Max(s.Widths[i], s.Widths[i + 1]) / 2);
            if (Geo.DistanceToSegment(p, new PointD(s.Xy[2 * i], s.Xy[(2 * i) + 1]), new PointD(s.Xy[2 * (i + 1)], s.Xy[(2 * (i + 1)) + 1])) <= reach)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A 72-sided polygon on an ellipse inscribed in a rectangle.</summary>
    internal static IReadOnlyList<PointD> EllipseRing(PdfRect r)
    {
        var c = Geo.Center(r);
        var (rx, ry) = (r.Width / 2, r.Height / 2);
        return [.. Enumerable.Range(0, 72).Select(i => new PointD(c.X + (rx * Math.Cos(i * Math.PI / 36)), c.Y + (ry * Math.Sin(i * Math.PI / 36))))];
    }
}
