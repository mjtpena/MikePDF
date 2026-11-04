
namespace MikePDF.Markup.Editing;

public enum HandleKind
{
    NorthWest,
    North,
    NorthEast,
    East,
    SouthEast,
    South,
    SouthWest,
    West,

    /// <summary>A vertex of a line, polygon or polyline; <see cref="Handle.VertexIndex"/> says which.</summary>
    Vertex,
}

/// <summary>A handle of a selected annotation, at a point of the page in user space.</summary>
public readonly record struct Handle(HandleKind Kind, PointD Position, int VertexIndex = -1);

/// <summary>What a selected annotation shows (annotation-core 3.4): eight handles for box kinds, vertex handles for lines and polygons, none for text markup, which shows an outline.</summary>
public static class SelectionGeometry
{
    /// <summary>The handles, in a stable order: the eight box handles first when the kind has them, then the vertices.</summary>
    public static IReadOnlyList<Handle> Handles(Annotation a)
    {
        ArgumentNullException.ThrowIfNull(a);
        if (a.ReplyKind != ReplyKind.None || a.IsTextAnchored)
        {
            return [];
        }

        var handles = new List<Handle>();
        if (HasBoxHandles(a))
        {
            handles.AddRange(BoxHandles(a.Rect));
        }

        switch (a.Payload)
        {
            case LinePayload line:
                handles.Add(new Handle(HandleKind.Vertex, line.Start, 0));
                handles.Add(new Handle(HandleKind.Vertex, line.End, 1));
                break;
            case PolyPayload poly:
                for (var i = 0; i < poly.Vertices.Count; i++)
                {
                    handles.Add(new Handle(HandleKind.Vertex, poly.Vertices[i], i));
                }

                break;
        }

        return handles;
    }

    /// <summary>Box handles exist for kinds that resize: rectangles, ellipses, text boxes (not typewriter text, which only moves), stamps, polygons and polylines. Notes and ink cannot be resized.</summary>
    public static bool HasBoxHandles(Annotation a) => a.Payload switch
    {
        BoxPayload or StampPayload or PolyPayload => true,
        FreeTextPayload text => !text.Typewriter,
        _ => false,
    };

    public static IReadOnlyList<Handle> BoxHandles(PdfRect r)
    {
        var cx = (r.Left + r.Right) / 2;
        var cy = (r.Bottom + r.Top) / 2;
        return
        [
            new Handle(HandleKind.NorthWest, new PointD(r.Left, r.Top)),
            new Handle(HandleKind.North, new PointD(cx, r.Top)),
            new Handle(HandleKind.NorthEast, new PointD(r.Right, r.Top)),
            new Handle(HandleKind.East, new PointD(r.Right, cy)),
            new Handle(HandleKind.SouthEast, new PointD(r.Right, r.Bottom)),
            new Handle(HandleKind.South, new PointD(cx, r.Bottom)),
            new Handle(HandleKind.SouthWest, new PointD(r.Left, r.Bottom)),
            new Handle(HandleKind.West, new PointD(r.Left, cy)),
        ];
    }

    /// <summary>The handle under a point, if any (the nearest within the tolerance; 24 DIP for touch, annotation-core 4).</summary>
    public static Handle? HitHandle(Annotation a, PointD point, double tolerance)
    {
        Handle? best = null;
        var bestDistance = tolerance;
        foreach (var handle in Handles(a))
        {
            var d = Geo.Distance(handle.Position, point);
            if (d <= bestDistance)
            {
                best = handle;
                bestDistance = d;
            }
        }

        return best;
    }

    /// <summary>The outline a selected text markup shows instead of handles: the polygons of its quads.</summary>
    public static IReadOnlyList<IReadOnlyList<PointD>> Outline(Annotation a) => a.Payload switch
    {
        TextMarkupPayload markup => [.. markup.Quads.Select(static q => (IReadOnlyList<PointD>)q.Outline())],
        _ => [[new PointD(a.Rect.Left, a.Rect.Bottom), new PointD(a.Rect.Right, a.Rect.Bottom), new PointD(a.Rect.Right, a.Rect.Top), new PointD(a.Rect.Left, a.Rect.Top)]],
    };
}

/// <summary>Move and resize rules with their clamps (annotation-core 3.4, AC-05).</summary>
public static class MoveResize
{
    /// <summary>The smallest a resized annotation gets, in points.</summary>
    public const double MinSize = 4;

    /// <summary>How much of an annotation stays inside the page when it is moved, in points on each axis.</summary>
    public const double MinInside = 8;

    public const double NudgePoints = 1;

    public const double LargeNudgePoints = 10;

    /// <summary>
    /// A move pulled back so that at least 8 x 8 points of the rectangle (all of it, if it is smaller) stay inside the page's displayed box.
    /// </summary>
    public static (double Dx, double Dy) ClampMove(PdfRect rect, double dx, double dy, PdfRect pageBox)
    {
        var keepX = Math.Min(MinInside, rect.Width);
        var keepY = Math.Min(MinInside, rect.Height);
        var left = Math.Clamp(rect.Left + dx, pageBox.Left + keepX - rect.Width, pageBox.Right - keepX);
        var bottom = Math.Clamp(rect.Bottom + dy, pageBox.Bottom + keepY - rect.Height, pageBox.Top - keepY);
        return (left - rect.Left, bottom - rect.Bottom);
    }

    /// <summary>
    /// Drags a handle by (dx, dy). <paramref name="keepAspect"/> (Shift) keeps the proportions at the corners; <paramref name="fromCentre"/> (Alt) mirrors
    /// the change on the opposite side. The result is at least 4 x 4 points and inside the page's displayed box.
    /// </summary>
    public static PdfRect Resize(PdfRect rect, HandleKind handle, double dx, double dy, bool keepAspect, bool fromCentre, PdfRect pageBox)
    {
        double left = rect.Left, right = rect.Right, bottom = rect.Bottom, top = rect.Top;
        var west = handle is HandleKind.NorthWest or HandleKind.West or HandleKind.SouthWest;
        var east = handle is HandleKind.NorthEast or HandleKind.East or HandleKind.SouthEast;
        var north = handle is HandleKind.NorthWest or HandleKind.North or HandleKind.NorthEast;
        var south = handle is HandleKind.SouthWest or HandleKind.South or HandleKind.SouthEast;
        if (west)
        {
            left += dx;
            right -= fromCentre ? dx : 0;
        }

        if (east)
        {
            right += dx;
            left -= fromCentre ? dx : 0;
        }

        if (north)
        {
            top += dy;
            bottom -= fromCentre ? dy : 0;
        }

        if (south)
        {
            bottom += dy;
            top -= fromCentre ? dy : 0;
        }

        if (keepAspect && (west || east) && (north || south) && rect.Width > Geo.Epsilon && rect.Height > Geo.Epsilon)
        {
            var scale = Math.Max((right - left) / rect.Width, (top - bottom) / rect.Height);
            var w = rect.Width * scale;
            var h = rect.Height * scale;
            if (fromCentre)
            {
                var c = Geo.Center(rect);
                (left, right, bottom, top) = (c.X - (w / 2), c.X + (w / 2), c.Y - (h / 2), c.Y + (h / 2));
            }
            else
            {
                if (west)
                {
                    left = right - w;
                }
                else
                {
                    right = left + w;
                }

                if (south)
                {
                    bottom = top - h;
                }
                else
                {
                    top = bottom + h;
                }
            }
        }

        // Never smaller than the minimum: the edge that moved stops.
        if (right - left < MinSize)
        {
            if (west)
            {
                left = right - MinSize;
            }
            else
            {
                right = left + MinSize;
            }
        }

        if (top - bottom < MinSize)
        {
            if (south)
            {
                bottom = top - MinSize;
            }
            else
            {
                top = bottom + MinSize;
            }
        }

        // Inside the displayed box.
        var inside = new PdfRect(Math.Max(left, pageBox.Left), Math.Max(bottom, pageBox.Bottom), Math.Min(right, pageBox.Right), Math.Min(top, pageBox.Top));
        return inside.Right - inside.Left < MinSize || inside.Top - inside.Bottom < MinSize ? new PdfRect(left, bottom, right, top) : inside;
    }

    /// <summary>The distance an arrow key moves an annotation (annotation-core 3.4: 1 point, 10 with Shift).</summary>
    public static double NudgeDistance(bool shift) => shift ? LargeNudgePoints : NudgePoints;
}

/// <summary>Vertex edits of lines, polygons and polylines (shapes 3.4, SH-05). Each returns a new annotation with its rectangle refitted, or null when the edit is refused.</summary>
public static class VertexEdit
{
    public static Annotation? Move(Annotation a, int index, PointD to)
    {
        switch (a.Payload)
        {
            case LinePayload line when index is 0 or 1:
                return MarkupGeometry.Refit(a with { Payload = index == 0 ? line with { Start = to } : line with { End = to } });
            case PolyPayload poly when index >= 0 && index < poly.Vertices.Count:
            {
                var vertices = poly.Vertices.ToList();
                vertices[index] = to;
                return MarkupGeometry.Refit(a with { Payload = poly with { Vertices = vertices } });
            }

            default:
                return null;
        }
    }

    /// <summary>Inserts a vertex after <paramref name="index"/>, at the midpoint of the segment to the next one (a polygon wraps around). Refused above 1,000 vertices.</summary>
    public static Annotation? Insert(Annotation a, int index)
    {
        if (a.Payload is not PolyPayload poly || index < 0 || index >= poly.Vertices.Count || poly.Vertices.Count >= ShapeGeometry.MaxVertices)
        {
            return null;
        }

        var closed = a.Kind == MarkupKind.Polygon;
        if (!closed && index == poly.Vertices.Count - 1)
        {
            return null;
        }

        var next = poly.Vertices[(index + 1) % poly.Vertices.Count];
        var vertices = poly.Vertices.ToList();
        vertices.Insert(index + 1, Geo.Mid(poly.Vertices[index], next));
        return MarkupGeometry.Refit(a with { Payload = poly with { Vertices = vertices } });
    }

    /// <summary>Removes a vertex. Refused below 3 vertices for a polygon and 2 for a polyline (the shape is not deleted).</summary>
    public static Annotation? Remove(Annotation a, int index)
    {
        if (a.Payload is not PolyPayload poly || index < 0 || index >= poly.Vertices.Count)
        {
            return null;
        }

        var minimum = a.Kind == MarkupKind.Polygon ? ShapeGeometry.MinPolygonVertices : ShapeGeometry.MinPolylineVertices;
        if (poly.Vertices.Count <= minimum)
        {
            return null;
        }

        var vertices = poly.Vertices.ToList();
        vertices.RemoveAt(index);
        return MarkupGeometry.Refit(a with { Payload = poly with { Vertices = vertices } });
    }

    /// <summary>Scales the vertices from the shape's bounding box onto a new box (the eight handles of a polygon).</summary>
    public static Annotation Scale(Annotation a, PdfRect target)
    {
        var bounds = Geo.Inflate(a.Rect, -a.Width / 2);
        var scaled = AnnotationTransform.ResizeTo(a with { Rect = bounds }, Geo.Inflate(target, -a.Width / 2));
        return MarkupGeometry.Refit(scaled);
    }
}
