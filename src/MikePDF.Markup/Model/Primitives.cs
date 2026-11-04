using System.Globalization;

namespace MikePDF.Markup;

/// <summary>Every annotation kind the v1.0 tools create (annotation-core 3.1). An arrow is a line with an arrow ending; the file cannot tell them apart.</summary>
public enum MarkupKind
{
    Highlight = 1,
    Underline,
    StrikeOut,
    Squiggly,
    Note,
    FreeText,
    Ink,
    Rectangle,
    Ellipse,
    Line,
    Arrow,
    Polygon,
    PolyLine,
    Stamp,
}

/// <summary>A colour as PDF writes it: red, green and blue, each 0 to 1.</summary>
public readonly record struct Rgb(double R, double G, double B)
{
    public static Rgb Black { get; } = new(0, 0, 0);

    public static Rgb White { get; } = new(1, 1, 1);

    /// <summary>The default highlight yellow (text-markup 2.1).</summary>
    public static Rgb Yellow { get; } = new(1, 0.92, 0);

    public static Rgb Red { get; } = new(1, 0, 0);

    /// <summary>WCAG relative luminance (sRGB).</summary>
    public double Luminance
    {
        get
        {
            static double Linear(double c) => c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            return (0.2126 * Linear(R)) + (0.7152 * Linear(G)) + (0.0722 * Linear(B));
        }
    }

    /// <summary>WCAG contrast ratio of two colours, 1 to 21.</summary>
    public static double Contrast(Rgb a, Rgb b)
    {
        var (hi, lo) = a.Luminance >= b.Luminance ? (a.Luminance, b.Luminance) : (b.Luminance, a.Luminance);
        return (hi + 0.05) / (lo + 0.05);
    }

    public float[] ToFloats() => [(float)R, (float)G, (float)B];

    public static Rgb FromFloats(float[] values) => new(values[0], values[1], values[2]);

    public static Rgb FromNumbers(IReadOnlyList<double> values) => new(values[0], values[1], values[2]);

    public bool IsValid => R is >= 0 and <= 1 && G is >= 0 and <= 1 && B is >= 0 and <= 1;
}

/// <summary>How a stroke is dashed (shapes 3.2).</summary>
public enum DashStyle
{
    Solid,
    Dashed,
    Dotted,
}

/// <summary>The end of a line or polyline (shapes 3.2).</summary>
public enum LineEnding
{
    None,
    OpenArrow,
    ClosedArrow,
    Square,
    Circle,
    Diamond,
}

/// <summary>The seven standard note icons (sticky-notes 2.1). Names are the ISO 32000 ones and are written as name objects.</summary>
public enum NoteIcon
{
    Comment,
    Note,
    Key,
    Help,
    NewParagraph,
    Paragraph,
    Insert,
}

/// <summary>The review states of a note thread (sticky-notes 2.3).</summary>
public enum ReviewState
{
    None,
    Accepted,
    Rejected,
    Cancelled,
    Completed,
}

public enum TextAlign
{
    Left,
    Center,
    Right,
}

/// <summary>The three base families of free text (free-text 3.2).</summary>
public enum FontFamily
{
    Sans,
    Serif,
    Mono,
}

/// <summary>How a reply relates to the annotation it answers.</summary>
public enum ReplyKind
{
    /// <summary>An ordinary annotation.</summary>
    None,

    /// <summary>A reply: a Text annotation with <c>/IRT</c> and <c>/RT /R</c>.</summary>
    Reply,

    /// <summary>A review state change: a Text annotation with <c>/StateModel /Review</c> and <c>/State</c>.</summary>
    State,
}

/// <summary>The class an annotation falls in, which decides what may be done to it (annotation-core 3.2).</summary>
public enum AnnotationClass
{
    /// <summary><c>/NM</c> starts with <c>MikePDF:</c>: everything is allowed.</summary>
    Owned,

    /// <summary>A kind MikePDF can draw, made elsewhere: select, delete, reply, state, move; edit comments by the same author.</summary>
    ForeignSupported,

    /// <summary>Anything else: read only.</summary>
    Unsupported,
}

/// <summary>Where an annotation came from.</summary>
public enum AnnotationOrigin
{
    /// <summary>Made in this session.</summary>
    Created,

    /// <summary>Read from the file.</summary>
    Loaded,
}

/// <summary>Number formatting for content streams and dictionaries: invariant, no exponent, at most 4 decimals, never negative zero.</summary>
public static class Num
{
    public static string Format(double value)
    {
        if (!double.IsFinite(value))
        {
            return "0";
        }

        var rounded = Math.Round(value, 4, MidpointRounding.AwayFromZero);
        return rounded == 0 ? "0" : rounded.ToString("0.####", CultureInfo.InvariantCulture);
    }
}
