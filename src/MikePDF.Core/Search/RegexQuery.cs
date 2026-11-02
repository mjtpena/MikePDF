using System.Globalization;
using System.Text.RegularExpressions;
using MikePDF.Core.Reading;
using MikePDF.Core.Text;
using MikePDF.Protocol;

namespace MikePDF.Core.Search;

public enum RegexProblem
{
    None,

    /// <summary>The box is empty.</summary>
    Empty,

    /// <summary>Not a pattern: unbalanced brackets, a bad escape, a quantifier with nothing before it.</summary>
    Invalid,

    /// <summary>Too long, or its counted repeats would build too large a machine to compile within reason.</summary>
    TooComplex,

    /// <summary>Valid, but uses something the linear-time engine cannot do: backreferences, lookarounds, atomic groups, conditionals.</summary>
    Unsupported,
}

public readonly record struct RegexCompilation(Regex? Regex, RegexProblem Problem)
{
    public bool IsOk => Regex is not null && Problem == RegexProblem.None;
}

/// <summary>
/// Compiles the pattern a person typed into the find box (master prompt 8.1) so that no pattern can hang the window. The tools are two:
/// <list type="bullet">
/// <item><see cref="RegexOptions.NonBacktracking"/>, which matches in time linear in the text whatever the pattern, so catastrophic backtracking
/// (<c>(a+)+$</c>) cannot happen; and a match timeout, as a backstop for the cost of building the matcher's states on a very large text.</item>
/// <item>A check <i>before</i> compiling, because building the regex has no timeout: the pattern is short, and counted repeats (<c>{n,m}</c>), which
/// the machine unrolls, are bounded one by one and in product.</item>
/// </list>
/// </summary>
public static partial class RegexQuery
{
    public const int MaxPatternLength = 128;

    /// <summary>The largest number in a <c>{n}</c>, <c>{n,}</c> or <c>{n,m}</c>.</summary>
    public const int MaxRepeatBound = 1000;

    /// <summary>
    /// The product of all the bounds in a pattern (nested repeats multiply). Measured: the matcher builds its states as it goes, and <c>(a{30}){30}</c>
    /// (900) takes 0.3 s on a page of 5000 characters, <c>(a{50}){50}</c> (2500) 1.2 s, <c>(a{70}){70}</c> (4900) more than 2 s, and .NET refuses
    /// <c>(a{100}){100}</c> outright. 1000 keeps the worst allowed pattern well under the timeout.
    /// </summary>
    public const long MaxRepeatProduct = 1000;

    public static readonly TimeSpan DefaultMatchTimeout = TimeSpan.FromSeconds(2);

    public static RegexCompilation TryCompile(string pattern, bool matchCase, TimeSpan? matchTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return new RegexCompilation(null, RegexProblem.Empty);
        }

        if (pattern.Length > MaxPatternLength || ExceedsRepeatLimits(pattern))
        {
            return new RegexCompilation(null, RegexProblem.TooComplex);
        }

        var options = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant | RegexOptions.Multiline | (matchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
        try
        {
            return new RegexCompilation(new Regex(pattern, options, matchTimeout ?? DefaultMatchTimeout), RegexProblem.None);
        }
        catch (NotSupportedException)
        {
            return new RegexCompilation(null, RegexProblem.Unsupported);
        }
        catch (ArgumentException)
        {
            return new RegexCompilation(null, RegexProblem.Invalid); // RegexParseException is an ArgumentException
        }
        catch (InsufficientExecutionStackException)
        {
            return new RegexCompilation(null, RegexProblem.TooComplex);
        }
    }

    // {n}, {n,} and {n,m}. Anything that looks like one is counted, escaped or inside a class or not: refusing a harmless pattern is better than compiling a bad one.
    [GeneratedRegex(@"\{\s*(\d+)\s*(?:,\s*(\d*)\s*)?\}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex Repeat();

    private static bool ExceedsRepeatLimits(string pattern)
    {
        long product = 1;
        foreach (Match repeat in Repeat().Matches(pattern))
        {
            long bound = 0;
            foreach (var group in new[] { repeat.Groups[1], repeat.Groups[2] })
            {
                if (group.Length == 0)
                {
                    continue;
                }

                if (group.Length > 6 || !long.TryParse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                {
                    return true;
                }

                bound = Math.Max(bound, value);
            }

            if (bound > MaxRepeatBound)
            {
                return true;
            }

            product *= Math.Max(1, bound);
            if (product > MaxRepeatProduct)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Searches pages with a regular expression, in the broker, over the characters the engine already reported for each page (the numbering search
/// results use). It plugs into <see cref="FindSession"/> where the engine's own search would. Matches that are empty are ignored; a page has at most
/// <see cref="MaxMatchesPerPage"/>. A page whose matching takes longer than the regex's timeout is a failed page (<see cref="Status.Unsupported"/>,
/// which a find session counts and does not retry), never an exception.
/// </summary>
public sealed class RegexSearchSource(Func<int, CancellationToken, Task<ReadReply<PageCharacters>>> characters, Regex regex) : ISearchSource
{
    public const int MaxMatchesPerPage = 2048;

    /// <summary>After this many pages in a row hit the timeout the pattern is given up on: the rest of the pages are reported as not searched without trying.</summary>
    public const int GiveUpAfterSlowPages = 3;

    private int _slowStreak;

    public async Task<ReadReply<PageMatches>> SearchAsync(int page, SearchOptions options, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _slowStreak) >= GiveUpAfterSlowPages)
        {
            return ReadReply.Fail<PageMatches>(Status.Unsupported, "the pattern is too slow for this document");
        }

        var chars = await characters(page, cancellationToken).ConfigureAwait(false);
        if (!chars.IsOk)
        {
            return ReadReply.Fail<PageMatches>(chars.Status, chars.Detail);
        }

        var result = Search(chars.Value!, regex);
        if (result.IsOk)
        {
            Volatile.Write(ref _slowStreak, 0);
        }
        else if (result.Status == Status.Unsupported)
        {
            Interlocked.Increment(ref _slowStreak);
        }

        return result;
    }

    /// <summary>The text a pattern is matched against: one character per entry of the page (so positions are the engine's), with line breaks as LF, the end-of-line hyphen as '-', and other control characters as spaces.</summary>
    public static string TextOf(PageCharacters page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return string.Create(page.Count, page, static (span, source) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                var unit = (char)source.Units[i];
                span[i] = unit switch
                {
                    '\r' or '\n' => '\n',
                    '\t' => '\t',
                    '\u0002' when (source.Flags[i] & PageCharacters.Hyphen) != 0 => '-',
                    < ' ' => ' ',
                    _ => unit,
                };
            }
        });
    }

    public static ReadReply<PageMatches> Search(PageCharacters page, Regex regex)
    {
        ArgumentNullException.ThrowIfNull(regex);
        var text = TextOf(page);
        var matches = new List<PageMatch>();
        var truncated = page.Truncated;
        try
        {
            foreach (var match in regex.EnumerateMatches(text))
            {
                if (match.Length == 0)
                {
                    continue;
                }

                if (matches.Count == MaxMatchesPerPage)
                {
                    truncated = true;
                    break;
                }

                matches.Add(new PageMatch(match.Index, match.Length));
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return ReadReply.Fail<PageMatches>(Status.Unsupported, "the pattern took too long on this page");
        }

        return new ReadReply<PageMatches>(Status.Ok, new PageMatches(matches, truncated));
    }
}
