using MikePDF.Core.Geometry;
using MikePDF.Ink;

namespace MikePDF.Markup;

public enum PaintMode
{
    Fill,
    Stroke,
    FillAndStroke,
}

public enum CapStyle
{
    Butt = 0,
    Round = 1,
    Square = 2,
}

public enum JoinStyle
{
    Miter = 0,
    Round = 1,
    Bevel = 2,
}

/// <summary>One thing the annotation draws.</summary>
public abstract record SceneItem;

/// <summary>A path with the paint it is drawn with. Coordinates are in the scene's frame.</summary>
public sealed record ScenePath(
    IReadOnlyList<PathSegment> Segments,
    PaintMode Paint,
    Rgb? Fill,
    Rgb? Stroke,
    double StrokeWidth,
    CapStyle Cap = CapStyle.Butt,
    JoinStyle Join = JoinStyle.Miter,
    double MiterLimit = 10,
    IReadOnlyList<double>? Dash = null) : SceneItem;

/// <summary>One line of text in a standard font; <c>X</c> and <c>Y</c> are the baseline's start.</summary>
public sealed record SceneText(string Text, int FontId, double Size, Rgb Color, double X, double Y) : SceneItem;

/// <summary>
/// What an annotation looks like, independent of any renderer (annotation-core 3.3 "shared geometry"). The overlay draws it with Win2D and the
/// writer turns it into content stream operators, so the two cannot disagree about a shape. <see cref="BBox"/> and <see cref="Matrix"/> are the
/// appearance form's; the items are in the form's own space (the form is mapped onto the annotation's rectangle by the reader).
/// </summary>
public sealed record Scene(
    IReadOnlyList<SceneItem> Items,
    PdfRect BBox,
    IReadOnlyList<double> Matrix,
    double Opacity,
    bool Multiply,
    bool TransparencyGroup)
{
    public static IReadOnlyList<double> Identity { get; } = [1, 0, 0, 1, 0, 0];

    public bool IsIdentityMatrix => Matrix.Count == 6 && Matrix[0] == 1 && Matrix[1] == 0 && Matrix[2] == 0 && Matrix[3] == 1 && Matrix[4] == 0 && Matrix[5] == 0;

    /// <summary>Every point the scene's paths visit (control points included), for tests and rough bounds.</summary>
    public IEnumerable<PointD> PathPoints() => Items.OfType<ScenePath>().SelectMany(static p => PathBuilder.Points(p.Segments));

    /// <summary>The text items.</summary>
    public IEnumerable<SceneText> Texts() => Items.OfType<SceneText>();
}
