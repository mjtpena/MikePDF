using MikePDF.Core.Geometry;

namespace MikePDF.Core.Text;

/// <summary>Where a search match is on the page, from the page's raw characters (the numbering search results use).</summary>
public static class MatchGeometry
{
    private enum Run
    {
        Undecided,
        Across,
        Down,
    }

    /// <summary>
    /// The boxes (page space) to paint for characters <c>[start, start + length)</c>: one per run of characters in a line, however the line
    /// runs on the page (across, or down when the text is turned within the page). Characters PDFium made up and characters with no extent
    /// do not widen a box.
    /// </summary>
    public static IReadOnlyList<RectD> RectsFor(PageCharacters characters, PageGeometry geometry, int start, int length)
    {
        ArgumentNullException.ThrowIfNull(characters);
        ArgumentNullException.ThrowIfNull(geometry);
        var end = (int)Math.Min((long)start + length, characters.Count);
        var rects = new List<RectD>();
        RectD? open = null;
        var run = Run.Undecided;
        for (var i = Math.Max(0, start); i < end; i++)
        {
            if ((characters.Flags[i] & (PageCharacters.Generated | PageCharacters.Hyphen)) == PageCharacters.Generated)
            {
                continue;
            }

            var box = geometry.PdfToPage(characters.BoxOf(i));
            if (box.Width <= 0 || box.Height <= 0)
            {
                continue;
            }

            if (open is { } current && Continues(current, box, ref run))
            {
                open = current.Union(box);
            }
            else
            {
                if (open is { } done)
                {
                    rects.Add(done);
                }

                open = box;
                run = Run.Undecided;
            }
        }

        if (open is { } last)
        {
            rects.Add(last);
        }

        return rects;
    }

    // The next character continues the run when it sits beside (or, in a run that goes down, below) the box so far: it overlaps the box in
    // the other direction by more than half of the smaller of the two, and the gap along the run is no more than a couple of character sizes.
    // The first two characters decide which way the run goes, so the first character of the next line is never taken for a continuation.
    private static bool Continues(RectD box, RectD next, ref Run run)
    {
        var overlapY = Math.Min(box.Bottom, next.Bottom) - Math.Max(box.Y, next.Y);
        var overlapX = Math.Min(box.Right, next.Right) - Math.Max(box.X, next.X);
        var size = Math.Max(Math.Max(box.Height, next.Height), Math.Max(box.Width, next.Width));
        var across = overlapY > 0.5 * Math.Min(box.Height, next.Height) && Math.Abs(next.X - box.Right) <= 2 * size && next.X >= box.X - next.Width;
        var down = overlapX > 0.5 * Math.Min(box.Width, next.Width) && Math.Abs(next.Y - box.Bottom) <= 2 * size && next.Y >= box.Y - next.Height;
        switch (run)
        {
            case Run.Across:
                return across;
            case Run.Down:
                return down;
            default:
                if (across)
                {
                    run = Run.Across;
                    return true;
                }

                if (down)
                {
                    run = Run.Down;
                    return true;
                }

                return false;
        }
    }
}
