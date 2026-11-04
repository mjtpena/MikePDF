using MikePDF.Markup.Fonts;

namespace MikePDF.Markup;

/// <summary>The three colours of the built-in stamps. Each reaches 4.5:1 contrast against white (stamps 5; ST-01).</summary>
public enum StampColour
{
    Green,
    Red,
    Blue,
}

/// <summary>The 14 standard stamp names of ISO 32000 (stamps 3.1), the English text MikePDF draws for each (OQ-ST-4) and its colour (OQ-ST-6).</summary>
public static class StampCatalog
{
    public static IReadOnlyList<(string Name, string Text, StampColour Colour)> Entries { get; } =
    [
        ("Approved", "APPROVED", StampColour.Green),
        ("Experimental", "EXPERIMENTAL", StampColour.Blue),
        ("NotApproved", "NOT APPROVED", StampColour.Red),
        ("AsIs", "AS IS", StampColour.Blue),
        ("Expired", "EXPIRED", StampColour.Red),
        ("NotForPublicRelease", "NOT FOR PUBLIC RELEASE", StampColour.Red),
        ("Confidential", "CONFIDENTIAL", StampColour.Red),
        ("Final", "FINAL", StampColour.Green),
        ("Sold", "SOLD", StampColour.Blue),
        ("Departmental", "DEPARTMENTAL", StampColour.Blue),
        ("ForComment", "FOR COMMENT", StampColour.Blue),
        ("TopSecret", "TOP SECRET", StampColour.Red),
        ("Draft", "DRAFT", StampColour.Blue),
        ("ForPublicRelease", "FOR PUBLIC RELEASE", StampColour.Green),
    ];

    public static IEnumerable<string> Names => Entries.Select(static e => e.Name);

    public static bool IsStandard(string? name) => name is not null && Entries.Any(e => e.Name == name);

    public static string TextOf(string name) => Entries.First(e => e.Name == name).Text;

    public static StampColour ColourOf(string name) => Entries.First(e => e.Name == name).Colour;

    public static Rgb Rgb(StampColour colour) => colour switch
    {
        StampColour.Green => new Rgb(0, 0.5, 0),
        StampColour.Red => new Rgb(0.75, 0, 0),
        _ => new Rgb(0, 0, 0.75),
    };

    public static Rgb RgbOf(string name) => Rgb(ColourOf(name));
}

/// <summary>The artwork of the built-in stamps (stamps 3.1, 3.6): a rounded outline and the stamp's text in bold capitals (Helvetica-Bold).</summary>
public static class StampArtwork
{
    public const double FontSize = 24;
    public const double PaddingX = 14;
    public const double PaddingY = 12;
    public const double BorderWidth = 3;
    public const double CornerRadius = 7;

    /// <summary>The Helvetica-Bold font number.</summary>
    public const int FontId = 1;

    /// <summary>The native size from the text width plus padding ("about 150 x 40 pt").</summary>
    public static (double Width, double Height) NativeSize(string name)
    {
        var text = StampCatalog.TextOf(name);
        var width = StandardFonts.Measure(text, FontId, FontSize) + (2 * PaddingX) + (2 * BorderWidth);
        var height = (StandardFonts.CapHeight(FontId) * FontSize / 1000.0) + (2 * PaddingY) + (2 * BorderWidth) - 4;
        return (width, height);
    }

    /// <summary>The items in the form's own space, which has its origin at the bottom-left of the native box.</summary>
    public static IReadOnlyList<SceneItem> Items(string name)
    {
        var colour = StampCatalog.RgbOf(name);
        var text = StampCatalog.TextOf(name);
        var (w, h) = NativeSize(name);
        var half = BorderWidth / 2;
        var outline = new PathBuilder().RoundedRectangle(half, half, w - half, h - half, CornerRadius).Segments;
        var cap = StandardFonts.CapHeight(FontId) * FontSize / 1000.0;
        var textWidth = StandardFonts.Measure(text, FontId, FontSize);
        return
        [
            new ScenePath(outline, PaintMode.Stroke, null, colour, BorderWidth, CapStyle.Butt, JoinStyle.Round),
            new SceneText(text, FontId, FontSize, colour, (w - textWidth) / 2, (h - cap) / 2),
        ];
    }
}
