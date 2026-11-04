using System.Text;
using MikePDF.Ink;
using MikePDF.Markup.Fonts;

namespace MikePDF.Markup;

/// <summary>A font the appearance's own <c>/Resources</c> declares: the name its content uses and the base font (always WinAnsi encoded, annotation-core 3.9 rule 6).</summary>
public sealed record FontResource(string ResourceName, string BaseFont);

/// <summary>
/// An appearance stream before it becomes PDF objects: the form's <c>/BBox</c> and <c>/Matrix</c>, its content (ASCII, so a string is exact), the
/// fonts it needs, and the transparency state (opacity for <c>/CA</c> and <c>/ca</c> of the <c>GS</c> graphics state, the blend mode, whether the form
/// is a transparency group).
/// </summary>
public sealed record AppearanceForm(
    PdfRect BBox,
    IReadOnlyList<double> Matrix,
    string Content,
    IReadOnlyList<FontResource> Fonts,
    double Opacity,
    bool Multiply,
    bool TransparencyGroup)
{
    /// <summary>The name of the graphics state in the form's resources.</summary>
    public const string GraphicsStateName = "GS";

    /// <summary>The largest appearance stream the writer will make (ink-pen 3.8).</summary>
    public const int MaxContentBytes = 2 * 1024 * 1024;
}

/// <summary>An appearance would be larger than <see cref="AppearanceForm.MaxContentBytes"/>.</summary>
public sealed class AppearanceTooLargeException(string message) : Exception(message);

/// <summary>Writes a <see cref="Scene"/> as content stream operators (the second half of "shared geometry": the paths are the scene's, not recomputed).</summary>
public static class AppearanceWriter
{
    public static AppearanceForm Write(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var content = new StringBuilder(1024);
        var fonts = new Dictionary<string, FontResource>(StringComparer.Ordinal);
        content.Append('/').Append(AppearanceForm.GraphicsStateName).Append(" gs\n");
        foreach (var item in scene.Items)
        {
            switch (item)
            {
                case ScenePath path:
                    WritePath(content, path);
                    break;
                case SceneText text:
                    var resource = StandardFonts.ResourceName(text.FontId);
                    fonts.TryAdd(resource, new FontResource(resource, StandardFonts.BaseFont(text.FontId)));
                    content.Append("BT\n/").Append(resource).Append(' ').Append(Num.Format(text.Size)).Append(" Tf\n");
                    content.Append(Colour(text.Color)).Append(" rg\n");
                    content.Append(Num.Format(text.X)).Append(' ').Append(Num.Format(text.Y)).Append(" Td\n");
                    content.Append(WinAnsi.ToLiteral(text.Text)).Append(" Tj\nET\n");
                    break;
            }

            if (content.Length > AppearanceForm.MaxContentBytes)
            {
                throw new AppearanceTooLargeException("The appearance would be over 2 MiB.");
            }
        }

        return new AppearanceForm(scene.BBox, scene.Matrix, content.ToString(), [.. fonts.Values], scene.Opacity, scene.Multiply, scene.TransparencyGroup);
    }

    private static void WritePath(StringBuilder content, ScenePath path)
    {
        content.Append("q\n");
        if (path.Fill is { } fill && path.Paint != PaintMode.Stroke)
        {
            content.Append(Colour(fill)).Append(" rg\n");
        }

        if (path.Stroke is { } stroke && path.Paint != PaintMode.Fill)
        {
            content.Append(Colour(stroke)).Append(" RG\n");
            content.Append(Num.Format(path.StrokeWidth)).Append(" w\n");
            content.Append((int)path.Cap).Append(" J\n").Append((int)path.Join).Append(" j\n");
            if (path.Join == JoinStyle.Miter)
            {
                content.Append(Num.Format(path.MiterLimit)).Append(" M\n");
            }

            if (path.Dash is { Count: > 0 } dash)
            {
                content.Append('[').Append(string.Join(' ', dash.Select(Num.Format))).Append("] 0 d\n");
            }
        }

        foreach (var s in path.Segments)
        {
            switch (s.Kind)
            {
                case PathSegmentKind.MoveTo:
                    content.Append(Num.Format(s.X1)).Append(' ').Append(Num.Format(s.Y1)).Append(" m\n");
                    break;
                case PathSegmentKind.LineTo:
                    content.Append(Num.Format(s.X1)).Append(' ').Append(Num.Format(s.Y1)).Append(" l\n");
                    break;
                case PathSegmentKind.CubicTo:
                    content.Append(Num.Format(s.X1)).Append(' ').Append(Num.Format(s.Y1)).Append(' ')
                        .Append(Num.Format(s.X2)).Append(' ').Append(Num.Format(s.Y2)).Append(' ')
                        .Append(Num.Format(s.X3)).Append(' ').Append(Num.Format(s.Y3)).Append(" c\n");
                    break;
                default:
                    content.Append("h\n");
                    break;
            }
        }

        content.Append(path.Paint switch { PaintMode.Fill => "f", PaintMode.Stroke => "S", _ => "B" }).Append("\nQ\n");
    }

    private static string Colour(Rgb c) => $"{Num.Format(c.R)} {Num.Format(c.G)} {Num.Format(c.B)}";
}
