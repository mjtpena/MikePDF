namespace MikePDF.Core.Colour;

/// <summary>How the pages are coloured on screen. The file and the engine's pixels never change; the GPU colours them when they are drawn.</summary>
public enum PageColourMode
{
    /// <summary>As the file draws them.</summary>
    Normal = 0,

    /// <summary>Dark pages for night reading: lightness inverted, hue kept.</summary>
    Inverted = 1,

    /// <summary>A warm paper tint.</summary>
    Sepia = 2,
}

/// <summary>
/// A colour matrix for a page colour mode, in the layout of Win2D's <c>Matrix5x4</c> and Direct2D's colour matrix effect: the output row
/// <c>[R' G' B' A']</c> is <c>[R G B A 1] x M</c>, so <see cref="Values"/> holds M row by row (rows: input R, G, B, A and the constant 1; columns:
/// output R, G, B, A). Colours are 0 to 1 and the result is clamped. Pure arithmetic with no UI types, so the maths is unit tested.
/// </summary>
public sealed class PageColourMatrix
{
    // Dark pages: CSS invert(1) hue-rotate(180deg) -- invert, then rotate the hue back, which leaves hue where it was and turns lightness over.
    // H is the hue-rotate(180deg) matrix (rows sum to 1, so grey maps to grey), the result is 1 - H*c, eased into [Low, High] so black text on white
    // becomes about 95% white on about 7% grey, not pure white on pure black (less glare, less halation).
    private const float Low = 0.07f;
    private const float High = 0.95f;
    private static readonly float[,] HueHalfTurn =
    {
        { -0.574f, 1.430f, 0.144f },
        { 0.426f, 0.430f, 0.144f },
        { 0.426f, 1.430f, -0.856f },
    };

    // Sepia: 20% toward the luminance, then multiplied by the colour of warm paper (white becomes #F4ECD8).
    private const float SepiaKeep = 0.8f;
    private const float SepiaGrey = 0.2f;
    private static readonly float[] Luminance = [0.2126f, 0.7152f, 0.0722f];
    private static readonly float[] PaperTint = [244f / 255f, 236f / 255f, 216f / 255f];

    private PageColourMatrix(float[] values) => Values = values;

    /// <summary>The 20 entries of the matrix, row by row.</summary>
    public IReadOnlyList<float> Values { get; }

    public bool IsIdentity => Values.SequenceEqual(Identity.Values);

    public static PageColourMatrix Identity { get; } = new([1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0]);

    public static PageColourMatrix For(PageColourMode mode) => mode switch
    {
        PageColourMode.Inverted => InvertedMatrix,
        PageColourMode.Sepia => SepiaMatrix,
        _ => Identity,
    };

    private static PageColourMatrix InvertedMatrix { get; } = Build(color: (output, input) => -(High - Low) * HueHalfTurn[output, input], offset: _ => Low + (High - Low));

    private static PageColourMatrix SepiaMatrix { get; } = Build(
        color: (output, input) => PaperTint[output] * ((input == output ? SepiaKeep : 0f) + (SepiaGrey * Luminance[input])),
        offset: _ => 0f);

    /// <summary>Maps one colour (each channel 0 to 1) through the matrix. The alpha channel is left alone.</summary>
    public (float R, float G, float B) Apply(float r, float g, float b)
    {
        var v = Values;
        float Channel(int column) => Math.Clamp((r * v[column]) + (g * v[4 + column]) + (b * v[8 + column]) + v[16 + column], 0f, 1f);
        return (Channel(0), Channel(1), Channel(2));
    }

    /// <summary>Maps an 8-bit colour; rounds to the nearest value.</summary>
    public (byte R, byte G, byte B) Apply(byte r, byte g, byte b)
    {
        var (nr, ng, nb) = Apply(r / 255f, g / 255f, b / 255f);
        return ((byte)Math.Round(nr * 255f), (byte)Math.Round(ng * 255f), (byte)Math.Round(nb * 255f));
    }

    // color(output, input) is the weight of input channel `input` in output channel `output`; the matrix stores it transposed (rows are inputs).
    private static PageColourMatrix Build(Func<int, int, float> color, Func<int, float> offset)
    {
        var values = new float[20];
        for (var input = 0; input < 3; input++)
        {
            for (var output = 0; output < 3; output++)
            {
                values[(input * 4) + output] = color(output, input);
            }
        }

        values[15] = 1f; // alpha stays alpha
        for (var output = 0; output < 3; output++)
        {
            values[16 + output] = offset(output);
        }

        return new PageColourMatrix(values);
    }
}
