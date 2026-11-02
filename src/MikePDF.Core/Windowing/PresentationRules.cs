namespace MikePDF.Core.Windowing;

/// <summary>Rules of presentation mode that need no window (docs/features/page-view-modes.md 3.6, 6).</summary>
public static class PresentationRules
{
    /// <summary>
    /// Whether a change of the displays ends the presentation: the display it started on is gone. When Windows gives no list at all nothing is known, so the
    /// presentation goes on.
    /// </summary>
    public static bool ShouldEndOnDisplayChange(string startedOn, IReadOnlyCollection<string> displaysNow)
    {
        ArgumentNullException.ThrowIfNull(displaysNow);
        return displaysNow.Count > 0 && !displaysNow.Contains(startedOn, StringComparer.Ordinal);
    }

    /// <summary>The page a typed number names, one-based in and zero-based out, kept inside the document; null when nothing usable was typed.</summary>
    public static int? PageFromTyped(string typed, int pageCount)
    {
        ArgumentNullException.ThrowIfNull(typed);
        if (pageCount <= 0 || typed.Length == 0 || typed.Length > 8 || !typed.All(char.IsAsciiDigit) || !int.TryParse(typed, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number))
        {
            return null;
        }

        return Math.Clamp(number, 1, pageCount) - 1;
    }
}
