using System.Globalization;
using System.Text;
using Quire.Core.Geometry;
using Quire.Core.Search;
using Quire.Core.Text;
using Quire.Protocol;

namespace Quire.Core.Reading;

/// <summary>
/// The reader's view of the engine's reading services: pages through the windows each reply is limited to, checks that the windows agree with
/// each other (the engine is untrusted, and a page that changes between two windows is not a page), cuts every list at a bound and turns the
/// protocol's replies into the types the reader uses. It never throws for what the engine says: a failure is a status.
/// </summary>
public sealed class DocumentReader(IReadingEngine engine, int pageCount) : ISearchSource
{
    public const int OutlineWindowSize = 64;

    /// <summary>Most bookmarks shown under one parent. A level with more is cut here and says so.</summary>
    public const int MaxOutlineEntriesPerLevel = 1000;

    public const int LinkWindowSize = 512;

    /// <summary>Most links read from one page.</summary>
    public const int MaxLinksPerPage = 4096;

    public const int TextWindowSize = 2048;

    /// <summary>The engine reports at most this many characters of a page.</summary>
    public const int MaxCharactersPerPage = 200_000;

    public const int MaxQueryBytes = 512;

    public const int MaxOutlineDepth = 16;

    public int PageCount { get; } = pageCount;

    // ---- outline -------------------------------------------------------------------------------------------------------------

    /// <summary>One window of the bookmarks under the bookmark at <paramref name="path"/> (empty: the top level).</summary>
    public async Task<ReadReply<OutlineWindow>> GetOutlineWindowAsync(IReadOnlyList<int> path, int first, int count, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Count > MaxOutlineDepth || first < 0 || count is < 1 or > OutlineWindowSize || path.Any(i => i < 0))
        {
            return ReadReply.Fail<OutlineWindow>(Status.InvalidArgument, "outline request out of bounds");
        }

        var reply = await engine.GetOutlineAsync([.. path.Select(i => (uint)i)], (uint)first, (uint)count, cancellationToken).ConfigureAwait(false);
        if (!reply.IsOk)
        {
            return ReadReply.Fail<OutlineWindow>(reply.Status, reply.Detail);
        }

        var r = reply.Value!;
        if (!Fits((uint)first, (uint)count, r.Total, r.Titles.Length) || r.Kinds.Length != r.Titles.Length || r.Pages.Length != r.Titles.Length
            || r.Xs.Length != r.Titles.Length || r.Ys.Length != r.Titles.Length || r.HasChildren.Length != r.Titles.Length)
        {
            return ReadReply.Fail<OutlineWindow>(Status.Damaged, "the outline window is not what was asked for");
        }

        var entries = new List<OutlineEntry>(r.Titles.Length);
        for (var i = 0; i < r.Titles.Length; i++)
        {
            var destination = ToDestination(r.Kinds[i], r.Pages[i], r.Xs[i], r.Ys[i]);
            if (destination is null)
            {
                return ReadReply.Fail<OutlineWindow>(Status.Damaged, "a bookmark names a page the document does not have");
            }

            entries.Add(new OutlineEntry(DisplayText.Clean(r.Titles[i]), destination.Value, r.HasChildren[i] != 0));
        }

        return new ReadReply<OutlineWindow>(Status.Ok, new OutlineWindow((int)r.Total, entries));
    }

    /// <summary>All the bookmarks under one parent, up to <see cref="MaxOutlineEntriesPerLevel"/>, a window at a time.</summary>
    public async Task<ReadReply<OutlineWindow>> GetOutlineLevelAsync(IReadOnlyList<int> path, CancellationToken cancellationToken)
    {
        var entries = new List<OutlineEntry>();
        var total = -1;
        while (true)
        {
            var window = await GetOutlineWindowAsync(path, entries.Count, OutlineWindowSize, cancellationToken).ConfigureAwait(false);
            if (!window.IsOk)
            {
                return window;
            }

            var value = window.Value!;
            if (total >= 0 && value.Total != total)
            {
                return ReadReply.Fail<OutlineWindow>(Status.Damaged, "the number of bookmarks changed while they were read");
            }

            total = value.Total;
            entries.AddRange(value.Entries);
            if (value.Entries.Count == 0 || entries.Count >= total || entries.Count >= MaxOutlineEntriesPerLevel)
            {
                break;
            }
        }

        if (entries.Count > MaxOutlineEntriesPerLevel)
        {
            entries.RemoveRange(MaxOutlineEntriesPerLevel, entries.Count - MaxOutlineEntriesPerLevel);
        }

        return new ReadReply<OutlineWindow>(Status.Ok, new OutlineWindow(total, entries));
    }

    // ---- links ---------------------------------------------------------------------------------------------------------------

    public async Task<ReadReply<PageLinks>> GetLinksAsync(int page, CancellationToken cancellationToken)
    {
        if (!IsPage(page))
        {
            return ReadReply.Fail<PageLinks>(Status.InvalidArgument, "page out of range");
        }

        var links = new List<PageLink>();
        var total = -1;
        while (true)
        {
            var first = links.Count;
            var reply = await engine.GetPageLinksAsync((uint)page, (uint)first, LinkWindowSize, cancellationToken).ConfigureAwait(false);
            if (!reply.IsOk)
            {
                return ReadReply.Fail<PageLinks>(reply.Status, reply.Detail);
            }

            var r = reply.Value!;
            var n = r.Lefts.Length;
            if (!Fits((uint)first, LinkWindowSize, r.Total, n) || r.Bottoms.Length != n || r.Rights.Length != n || r.Tops.Length != n || r.Kinds.Length != n
                || r.Pages.Length != n || r.Xs.Length != n || r.Ys.Length != n || (total >= 0 && r.Total != total))
            {
                return ReadReply.Fail<PageLinks>(Status.Damaged, "the link window is not what was asked for");
            }

            total = (int)r.Total;
            for (var i = 0; i < n; i++)
            {
                var destination = ToDestination(r.Kinds[i], r.Pages[i], r.Xs[i], r.Ys[i]);
                if (destination is null)
                {
                    return ReadReply.Fail<PageLinks>(Status.Damaged, "a link names a page the document does not have");
                }

                links.Add(new PageLink(first + i, PdfRect.FromCorners(r.Lefts[i], r.Bottoms[i], r.Rights[i], r.Tops[i]), destination.Value));
            }

            if (n == 0 || links.Count >= total || links.Count >= MaxLinksPerPage)
            {
                break;
            }
        }

        return new ReadReply<PageLinks>(Status.Ok, new PageLinks(links, links.Count < total));
    }

    /// <summary>The address behind an external link, classified for the confirmation the person is asked for.</summary>
    public async Task<ReadReply<ExternalAddress>> GetAddressAsync(int page, int linkIndex, CancellationToken cancellationToken)
    {
        if (!IsPage(page) || linkIndex < 0)
        {
            return ReadReply.Fail<ExternalAddress>(Status.InvalidArgument, "link out of range");
        }

        var reply = await engine.GetLinkUriAsync((uint)page, (uint)linkIndex, cancellationToken).ConfigureAwait(false);
        return reply.IsOk ? new ReadReply<ExternalAddress>(Status.Ok, ExternalAddress.Classify(reply.Value!.Uri)) : ReadReply.Fail<ExternalAddress>(reply.Status, reply.Detail);
    }

    // ---- page geometry and text ----------------------------------------------------------------------------------------------

    public async Task<ReadReply<PageGeometry>> GetGeometryAsync(int page, CancellationToken cancellationToken)
    {
        if (!IsPage(page))
        {
            return ReadReply.Fail<PageGeometry>(Status.InvalidArgument, "page out of range");
        }

        var reply = await engine.GetPageBoxesAsync((uint)page, cancellationToken).ConfigureAwait(false);
        if (!reply.IsOk)
        {
            return ReadReply.Fail<PageGeometry>(reply.Status, reply.Detail);
        }

        var r = reply.Value!;
        var media = PdfRect.FromCorners(r.MediaLeft, r.MediaBottom, r.MediaRight, r.MediaTop);
        if (media.IsEmpty)
        {
            media = new PdfRect(0, 0, 612, 792); // a page with no usable size is drawn as US Letter, as viewers do
        }

        PdfRect? crop = r.HasCropBox ? PdfRect.FromCorners(r.CropLeft, r.CropBottom, r.CropRight, r.CropTop) : null;
        return new ReadReply<PageGeometry>(Status.Ok, new PageGeometry(media, crop, r.Rotation * 90));
    }

    /// <summary>All the characters of a page, window by window.</summary>
    public async Task<ReadReply<PageCharacters>> GetCharactersAsync(int page, CancellationToken cancellationToken)
    {
        if (!IsPage(page))
        {
            return ReadReply.Fail<PageCharacters>(Status.InvalidArgument, "page out of range");
        }

        ushort[]? units = null;
        float[]? lefts = null;
        float[]? bottoms = null;
        float[]? rights = null;
        float[]? tops = null;
        byte[]? flags = null;
        var truncated = false;
        var filled = 0;
        while (true)
        {
            var reply = await engine.GetPageTextAsync((uint)page, (uint)filled, TextWindowSize, cancellationToken).ConfigureAwait(false);
            if (!reply.IsOk)
            {
                return ReadReply.Fail<PageCharacters>(reply.Status, reply.Detail);
            }

            var r = reply.Value!;
            var n = r.Units.Length;
            if (r.Total > MaxCharactersPerPage || !Fits((uint)filled, TextWindowSize, r.Total, n) || r.Lefts.Length != n || r.Bottoms.Length != n || r.Rights.Length != n
                || r.Tops.Length != n || r.Flags.Length != n || (units is not null && (r.Total != units.Length || r.Truncated != truncated)))
            {
                return ReadReply.Fail<PageCharacters>(Status.Damaged, "the text window is not what was asked for");
            }

            if (units is null)
            {
                var total = (int)r.Total;
                units = new ushort[total];
                lefts = new float[total];
                bottoms = new float[total];
                rights = new float[total];
                tops = new float[total];
                flags = new byte[total];
                truncated = r.Truncated;
            }

            r.Units.CopyTo(units, filled);
            r.Lefts.CopyTo(lefts!, filled);
            r.Bottoms.CopyTo(bottoms!, filled);
            r.Rights.CopyTo(rights!, filled);
            r.Tops.CopyTo(tops!, filled);
            for (var i = 0; i < n; i++)
            {
                flags![filled + i] = (byte)r.Flags[i];
            }

            filled += n;
            if (n == 0 || filled >= units.Length)
            {
                break;
            }
        }

        return new ReadReply<PageCharacters>(Status.Ok, new PageCharacters(units!, lefts!, bottoms!, rights!, tops!, flags!, truncated));
    }

    // ---- search --------------------------------------------------------------------------------------------------------------

    public async Task<ReadReply<PageMatches>> SearchAsync(int page, SearchOptions options, CancellationToken cancellationToken)
    {
        var query = options.Query;
        if (!IsPage(page) || options.IsEmpty || query.Contains('\0', StringComparison.Ordinal) || !IsEncodable(query) || Encoding.UTF8.GetByteCount(query) > MaxQueryBytes)
        {
            return ReadReply.Fail<PageMatches>(Status.InvalidArgument, "search request out of bounds");
        }

        var reply = await engine.SearchPageAsync((uint)page, query, options.Flags, cancellationToken).ConfigureAwait(false);
        if (!reply.IsOk)
        {
            return ReadReply.Fail<PageMatches>(reply.Status, reply.Detail);
        }

        var r = reply.Value!;
        if (r.Starts.Length != r.Lengths.Length)
        {
            return ReadReply.Fail<PageMatches>(Status.Damaged, "the match lists differ in length");
        }

        var matches = new PageMatch[r.Starts.Length];
        for (var i = 0; i < matches.Length; i++)
        {
            if ((long)r.Starts[i] + r.Lengths[i] > MaxCharactersPerPage || (i > 0 && r.Starts[i] <= r.Starts[i - 1]))
            {
                return ReadReply.Fail<PageMatches>(Status.Damaged, "the matches are not in order");
            }

            matches[i] = new PageMatch((int)r.Starts[i], (int)r.Lengths[i]);
        }

        return new ReadReply<PageMatches>(Status.Ok, new PageMatches(matches, r.Truncated));
    }

    // ---- helpers -------------------------------------------------------------------------------------------------------------

    private bool IsPage(int page) => page >= 0 && page < PageCount;

    // The engine's window rule (the broker checks it too): every entry from `first` up to `count`, and none past the end.
    private static bool Fits(uint first, uint count, uint total, int length) =>
        first >= total ? length == 0 : (ulong)length == Math.Min(count, total - first);

    private static bool IsEncodable(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(text[i]))
            {
                return false;
            }
        }

        return true;
    }

    private Destination? ToDestination(ushort kind, uint page, float x, float y)
    {
        if (kind > (ushort)DestinationKind.Other)
        {
            return null;
        }

        var destination = new Destination((DestinationKind)kind, (int)page, x, y);
        return destination.IsInternal && (int)page >= PageCount ? null : destination;
    }
}

/// <summary>Text that came out of a file, made fit to show: no control characters, no characters that reverse or hide text, no runs of spaces.</summary>
public static class DisplayText
{
    public static string Clean(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var clean = new StringBuilder(text.Length);
        var lastWasSpace = true;
        foreach (var rune in text.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Format or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned or UnicodeCategory.Surrogate)
            {
                continue;
            }

            if (category is UnicodeCategory.Control or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.SpaceSeparator)
            {
                if (!lastWasSpace)
                {
                    clean.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            clean.Append(rune.ToString());
            lastWasSpace = false;
        }

        return clean.ToString().TrimEnd();
    }
}
