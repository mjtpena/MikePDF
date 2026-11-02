using Quire.Core.Geometry;

namespace Quire.Core.Text;

/// <summary>
/// The characters of one page as the engine reports them (<c>GetPageText</c>): one entry per UTF-16 code unit in PDFium's order, each with its
/// box in PDF user space. This is raw material, in the numbering that search results use; <see cref="TextModelBuilder"/> turns it into text in
/// reading order. Immutable once built; the arrays belong to this object.
/// </summary>
public sealed class PageCharacters
{
    /// <summary>PDFium made the character up (a space or a line break between text objects).</summary>
    public const byte Generated = 1;

    /// <summary>A hyphen at the end of a line that PDFium replaced with U+0002.</summary>
    public const byte Hyphen = 2;

    public PageCharacters(ushort[] units, float[] lefts, float[] bottoms, float[] rights, float[] tops, byte[] flags, bool truncated)
    {
        ArgumentNullException.ThrowIfNull(units);
        ArgumentNullException.ThrowIfNull(lefts);
        ArgumentNullException.ThrowIfNull(bottoms);
        ArgumentNullException.ThrowIfNull(rights);
        ArgumentNullException.ThrowIfNull(tops);
        ArgumentNullException.ThrowIfNull(flags);
        if (lefts.Length != units.Length || bottoms.Length != units.Length || rights.Length != units.Length || tops.Length != units.Length || flags.Length != units.Length)
        {
            throw new ArgumentException("The arrays must have one entry per character.");
        }

        Units = units;
        Lefts = lefts;
        Bottoms = bottoms;
        Rights = rights;
        Tops = tops;
        Flags = flags;
        Truncated = truncated;
    }

    public static PageCharacters Empty { get; } = new([], [], [], [], [], [], false);

    public int Count => Units.Length;

    public ushort[] Units { get; }

    public float[] Lefts { get; }

    public float[] Bottoms { get; }

    public float[] Rights { get; }

    public float[] Tops { get; }

    public byte[] Flags { get; }

    /// <summary>The page has more characters than the engine reports.</summary>
    public bool Truncated { get; }

    public PdfRect BoxOf(int index) => PdfRect.FromCorners(Lefts[index], Bottoms[index], Rights[index], Tops[index]);

    /// <summary>The page's characters as a string, without the ones PDFium made up (a plain-text view for tests and diagnostics).</summary>
    public string PlainText()
    {
        var text = new System.Text.StringBuilder(Count);
        for (var i = 0; i < Count; i++)
        {
            if ((Flags[i] & Generated) == 0)
            {
                text.Append((char)Units[i]);
            }
        }

        return text.ToString();
    }
}
