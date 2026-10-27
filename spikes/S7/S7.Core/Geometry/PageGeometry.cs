using S7.Core.Engine;

namespace S7.Core.Geometry;

public readonly record struct PageRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public static PageRect Union(PageRect a, PageRect b)
    {
        double l = Math.Min(a.X, b.X), t = Math.Min(a.Y, b.Y), r = Math.Max(a.Right, b.Right), bt = Math.Max(a.Bottom, b.Bottom);
        return new PageRect(l, t, r - l, bt - t);
    }
}

/// <summary>
/// Independent implementation of master prompt 6.3 conversions (PDF user space to page space to view space).
///   user space : points, origin bottom-left, raw coordinates (not relative to the CropBox)
///   page space : rotated by /Rotate plus the view rotation, origin top-left, points, relative to the visible box
///   view space : page space multiplied by zoom (DIPs)
/// Validated against FPDF_PageToDevice in the A11yTest project for all four effective rotations.
/// </summary>
public sealed class PageGeometry
{
    private readonly UserRect _crop;
    private readonly int _q;   // effective quarter turns clockwise = (page /Rotate / 90 + view rotation) mod 4

    public PageGeometry(PageBox box, int viewRotationQuarterTurns)
    {
        _crop = box.Crop;
        _q = ((box.Rotate / 90 + viewRotationQuarterTurns) % 4 + 4) % 4;
        double w = _crop.Width, h = _crop.Height;
        (PageWidth, PageHeight) = (_q & 1) == 0 ? (w, h) : (h, w);
    }

    public double PageWidth { get; }
    public double PageHeight { get; }
    public int EffectiveQuarterTurns => _q;

    /// <summary>User space point to page space (top-left origin, points).</summary>
    public (double x, double y) UserToPage(double ux, double uy)
    {
        double dx = ux - _crop.Left, dy = uy - _crop.Bottom;
        double w = _crop.Width, h = _crop.Height;
        return _q switch
        {
            0 => (dx, h - dy),
            1 => (dy, dx),
            2 => (w - dx, dy),
            _ => (h - dy, w - dx),
        };
    }

    /// <summary>Page space point to user space (inverse of <see cref="UserToPage"/>).</summary>
    public (double x, double y) PageToUser(double x, double y)
    {
        double w = _crop.Width, h = _crop.Height;
        double dx, dy;
        switch (_q)
        {
            case 0: dx = x; dy = h - y; break;
            case 1: dy = x; dx = y; break;
            case 2: dx = w - x; dy = y; break;
            default: dy = h - x; dx = w - y; break;
        }
        return (dx + _crop.Left, dy + _crop.Bottom);
    }

    /// <summary>User-space rect to page-space rect (axis aligned bounding box of the transformed corners).</summary>
    public PageRect UserRectToPage(UserRect r)
    {
        var (x1, y1) = UserToPage(r.Left, r.Bottom);
        var (x2, y2) = UserToPage(r.Right, r.Top);
        double l = Math.Min(x1, x2), t = Math.Min(y1, y2);
        return new PageRect(l, t, Math.Abs(x2 - x1), Math.Abs(y2 - y1));
    }
}
