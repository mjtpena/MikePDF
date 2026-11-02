namespace MikePDF.Core.Geometry;

/// <summary>
/// The shape of one PDF page (master prompt 6.3): its MediaBox, CropBox and /Rotate. Converts between PDF user space (points, origin
/// bottom-left) and page space (points, rotation-normalised, origin top-left, y down).
/// <para>
/// PDFium shows the CropBox (clipped to the MediaBox) rotated clockwise by /Rotate, and that is the page: <see cref="Size"/> is what
/// <c>GetPageSizes</c> reports. The CropBox origin need not be (0, 0).
/// </para>
/// </summary>
public sealed class PageGeometry
{
    public PageGeometry(PdfRect mediaBox, PdfRect? cropBox, int rotateDegrees)
    {
        MediaBox = mediaBox;
        var visible = cropBox is { } crop ? crop.Intersect(mediaBox) : mediaBox;
        // A CropBox that misses the MediaBox altogether is ignored, as viewers do.
        Box = visible.IsEmpty ? mediaBox : visible;
        RotateTurns = QuarterTurns.FromDegrees(rotateDegrees);
    }

    public PdfRect MediaBox { get; }

    /// <summary>The visible box: the CropBox clipped to the MediaBox, or the MediaBox when there is no usable CropBox.</summary>
    public PdfRect Box { get; }

    /// <summary>The page's own /Rotate in quarter turns clockwise.</summary>
    public int RotateTurns { get; }

    /// <summary>Width and height of <see cref="Box"/> before /Rotate.</summary>
    public SizeD UnrotatedSize => new(Box.Width, Box.Height);

    /// <summary>The page as displayed (after /Rotate), in points.</summary>
    public SizeD Size => QuarterTurns.Rotate(UnrotatedSize, RotateTurns);

    public PointD PdfToPage(PointD pdf)
    {
        var unrotated = new PointD(pdf.X - Box.Left, Box.Top - pdf.Y);
        return QuarterTurns.Rotate(unrotated, UnrotatedSize, RotateTurns);
    }

    public PointD PageToPdf(PointD page)
    {
        var unrotated = QuarterTurns.RotateBack(page, Size, RotateTurns);
        return new PointD(unrotated.X + Box.Left, Box.Top - unrotated.Y);
    }

    /// <summary>The page-space bounding box of a PDF rectangle.</summary>
    public RectD PdfToPage(PdfRect rect) => Bounds(PdfToPage(new PointD(rect.Left, rect.Top)), PdfToPage(new PointD(rect.Right, rect.Bottom)));

    /// <summary>The PDF-space bounding box of a page-space rectangle.</summary>
    public PdfRect PageToPdf(RectD rect)
    {
        var a = PageToPdf(new PointD(rect.X, rect.Y));
        var b = PageToPdf(new PointD(rect.Right, rect.Bottom));
        return PdfRect.FromCorners(a.X, a.Y, b.X, b.Y);
    }

    internal static RectD Bounds(PointD a, PointD b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
}

/// <summary>
/// Maps one page to device pixels at a zoom and a view rotation: page space, then the view rotation (which does not modify the file), then
/// the scale. The scale is the one the engine really uses: it sizes the page to <c>llround(width * scale)</c> pixels, so the effective
/// per-axis scale differs very slightly from the requested one.
/// </summary>
public sealed class PageTransform
{
    public PageTransform(PageGeometry geometry, int viewTurns, double pixelsPerPoint)
        : this(geometry.Size, viewTurns, pixelsPerPoint, geometry)
    {
    }

    /// <summary>For a page known only by its displayed size (no boxes yet): no PDF-space conversion is available.</summary>
    public PageTransform(SizeD pageSize, int viewTurns, double pixelsPerPoint)
        : this(pageSize, viewTurns, pixelsPerPoint, null)
    {
    }

    private PageTransform(SizeD pageSize, int viewTurns, double pixelsPerPoint, PageGeometry? geometry)
    {
        if (!(pixelsPerPoint > 0) || double.IsInfinity(pixelsPerPoint))
        {
            throw new ArgumentOutOfRangeException(nameof(pixelsPerPoint));
        }

        Geometry = geometry;
        ViewTurns = QuarterTurns.Normalize(viewTurns);
        PageSize = pageSize;
        RotatedSize = QuarterTurns.Rotate(pageSize, ViewTurns);
        PixelWidth = EngineRounding(RotatedSize.Width * pixelsPerPoint);
        PixelHeight = EngineRounding(RotatedSize.Height * pixelsPerPoint);
        ScaleX = PixelWidth / RotatedSize.Width;
        ScaleY = PixelHeight / RotatedSize.Height;
    }

    public PageGeometry? Geometry { get; }

    public int ViewTurns { get; }

    /// <summary>Page space size (after /Rotate, before the view rotation), in points.</summary>
    public SizeD PageSize { get; }

    /// <summary>The size after the view rotation, in points.</summary>
    public SizeD RotatedSize { get; }

    public long PixelWidth { get; }

    public long PixelHeight { get; }

    public double ScaleX { get; }

    public double ScaleY { get; }

    /// <summary>Page space to device pixels, origin at the top-left of the rotated page.</summary>
    public PointD PageToDevice(PointD page)
    {
        var turned = QuarterTurns.Rotate(page, PageSize, ViewTurns);
        return new PointD(turned.X * ScaleX, turned.Y * ScaleY);
    }

    public PointD DeviceToPage(PointD device)
    {
        var turned = new PointD(device.X / ScaleX, device.Y / ScaleY);
        return QuarterTurns.RotateBack(turned, RotatedSize, ViewTurns);
    }

    public PointD PdfToDevice(PointD pdf) => PageToDevice(Require().PdfToPage(pdf));

    public PointD DeviceToPdf(PointD device) => Require().PageToPdf(DeviceToPage(device));

    public RectD PdfToDevice(PdfRect rect)
    {
        return PageGeometry.Bounds(PdfToDevice(new PointD(rect.Left, rect.Top)), PdfToDevice(new PointD(rect.Right, rect.Bottom)));
    }

    /// <summary>Rounds like the engine (<c>std::llround</c>: halves away from zero).</summary>
    public static long EngineRounding(double value) => (long)Math.Round(value, MidpointRounding.AwayFromZero);

    private PageGeometry Require() => Geometry ?? throw new InvalidOperationException("This transform was made without page boxes.");
}
