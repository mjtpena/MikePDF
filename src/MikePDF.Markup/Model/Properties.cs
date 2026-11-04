using System.Globalization;
using System.Text;

namespace MikePDF.Markup;

/// <summary>PDF date strings (7.9.4): <c>D:YYYYMMDDHHmmSS+HH'mm'</c> in local time with its offset (annotation-core 3.5).</summary>
public static class PdfDates
{
    public static string Format(DateTimeOffset value)
    {
        var offset = value.Offset;
        var sign = offset < TimeSpan.Zero ? '-' : '+';
        var abs = offset.Duration();
        return string.Create(CultureInfo.InvariantCulture, $"D:{value.Year:D4}{value.Month:D2}{value.Day:D2}{value.Hour:D2}{value.Minute:D2}{value.Second:D2}{sign}{abs.Hours:D2}'{abs.Minutes:D2}'");
    }

    /// <summary>Parses the forms real files use: with or without an offset, with <c>Z</c>, with the trailing apostrophe or without, and short forms down to the year.</summary>
    public static bool TryParse(string? text, out DateTimeOffset value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var s = text.Trim();
        if (s.StartsWith("D:", StringComparison.Ordinal))
        {
            s = s[2..];
        }

        var digits = 0;
        while (digits < s.Length && digits < 14 && char.IsAsciiDigit(s[digits]))
        {
            digits++;
        }

        if (digits < 4 || digits % 2 != 0)
        {
            return false;
        }

        int Part(int start, int length, int fallback) => digits >= start + length ? int.Parse(s.AsSpan(start, length), CultureInfo.InvariantCulture) : fallback;
        int year = Part(0, 4, 0), month = Part(4, 2, 1), day = Part(6, 2, 1), hour = Part(8, 2, 0), minute = Part(10, 2, 0), second = Part(12, 2, 0);
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(Math.Max(year, 1), month) || hour > 23 || minute > 59 || second > 59 || year < 1)
        {
            return false;
        }

        var offset = TimeSpan.Zero;
        var rest = s[digits..];
        if (rest.Length > 0 && rest[0] is '+' or '-')
        {
            var sign = rest[0] == '-' ? -1 : 1;
            var tail = rest[1..].Replace("'", string.Empty, StringComparison.Ordinal);
            if (tail.Length is not (2 or 4) || !tail.All(char.IsAsciiDigit))
            {
                return false;
            }

            var offsetHours = int.Parse(tail.AsSpan(0, 2), CultureInfo.InvariantCulture);
            var offsetMinutes = tail.Length == 4 ? int.Parse(tail.AsSpan(2, 2), CultureInfo.InvariantCulture) : 0;
            if (offsetHours > 23 || offsetMinutes > 59)
            {
                return false;
            }

            offset = sign * new TimeSpan(offsetHours, offsetMinutes, 0);
        }
        else if (rest.Length > 0 && rest[0] != 'Z')
        {
            return false;
        }

        value = new DateTimeOffset(year, month, day, hour, minute, second, offset);
        return true;
    }
}

/// <summary>Who an annotation's author is (annotation-core 3.5, AC-09): the setting, else the policy, else the Windows display name, else the user name.</summary>
public static class AuthorResolver
{
    public static string Resolve(string? setting, string? policy, string? windowsDisplayName, string? userName)
    {
        foreach (var candidate in new[] { setting, policy, windowsDisplayName, userName })
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate.Trim();
            }
        }

        return string.Empty;
    }
}

/// <summary>Comment text rules (annotation-core 3.5): NFC, no control characters other than tab and line breaks, at most 10,000 code points in the UI.</summary>
public static class CommentText
{
    public const int MaxCodePoints = 10_000;

    public static string Normalise(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var rune in text.Normalize(NormalizationForm.FormC).EnumerateRunes())
        {
            if (Rune.IsControl(rune) && rune.Value is not ('\t' or '\n' or '\r'))
            {
                continue;
            }

            builder.Append(rune.ToString());
        }

        return builder.ToString();
    }

    /// <summary>The length the limit counts: Unicode code points, not UTF-16 units (sticky-notes 6, SN-06).</summary>
    public static int CodePoints(string text) => text.EnumerateRunes().Count();

    /// <summary>The text cut to the limit at a code point boundary.</summary>
    public static string Truncate(string text, int maxCodePoints = MaxCodePoints)
    {
        var builder = new StringBuilder();
        var count = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (count++ >= maxCodePoints)
            {
                break;
            }

            builder.Append(rune.ToString());
        }

        return builder.ToString();
    }

    /// <summary>The first <paramref name="maxCodePoints"/> code points with an ellipsis if cut, for automation names (annotation-core 5).</summary>
    public static string Excerpt(string text, int maxCodePoints = 80) =>
        CodePoints(text) <= maxCodePoints ? text : Truncate(text, maxCodePoints) + "…";
}
