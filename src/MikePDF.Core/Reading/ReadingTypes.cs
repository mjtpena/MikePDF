using MikePDF.Core.Geometry;
using MikePDF.Protocol;

namespace MikePDF.Core.Reading;

/// <summary>What the engine answered, as the broker's typed result (§6.4) without the broker: a status and, for Ok, the reply.</summary>
public readonly record struct ReadReply<T>(Status Status, T? Value, string? Detail = null)
    where T : class
{
    public bool IsOk => Status == Status.Ok && Value is not null;
}

public static class ReadReply
{
    public static ReadReply<T> Fail<T>(Status status, string? detail = null)
        where T : class => new(status, null, detail);
}

/// <summary>
/// The engine's reading services (schema 3) as the reader needs them. The App implements it over the supervised document; tests implement it
/// with scripts, including hostile ones. Every method is one window of one list: the caller (<see cref="DocumentReader"/>) pages through.
/// </summary>
public interface IReadingEngine
{
    Task<ReadReply<GetOutlineReply>> GetOutlineAsync(IReadOnlyList<uint> path, uint first, uint count, CancellationToken cancellationToken);

    Task<ReadReply<GetPageLinksReply>> GetPageLinksAsync(uint page, uint first, uint count, CancellationToken cancellationToken);

    Task<ReadReply<GetLinkUriReply>> GetLinkUriAsync(uint page, uint index, CancellationToken cancellationToken);

    Task<ReadReply<GetPageBoxesReply>> GetPageBoxesAsync(uint page, CancellationToken cancellationToken);

    Task<ReadReply<GetPageTextReply>> GetPageTextAsync(uint page, uint first, uint count, CancellationToken cancellationToken);

    Task<ReadReply<SearchPageReply>> SearchPageAsync(uint page, string query, uint flags, CancellationToken cancellationToken);
}

/// <summary>Where a bookmark or a link goes.</summary>
public enum DestinationKind
{
    /// <summary>Nowhere (a destination that does not exist).</summary>
    None = 0,

    /// <summary>A page, from its top.</summary>
    Page = 1,

    /// <summary>A page, at a height.</summary>
    PageY = 2,

    /// <summary>A page, at a point.</summary>
    PageXY = 3,

    /// <summary>A web or mail address. Never followed without the person's say-so.</summary>
    External = 4,

    /// <summary>An action MikePDF does not follow: launch a program, open another file, run a script, a named action.</summary>
    Other = 5,
}

/// <summary>A place in a document: a page and, in PDF user space of that page, an optional point.</summary>
public readonly record struct Destination(DestinationKind Kind, int Page, double X, double Y)
{
    /// <summary>True for the kinds that name a page of this document.</summary>
    public bool IsInternal => Kind is DestinationKind.Page or DestinationKind.PageY or DestinationKind.PageXY;

    public bool HasY => Kind is DestinationKind.PageY or DestinationKind.PageXY;

    public bool HasX => Kind == DestinationKind.PageXY;
}

/// <summary>A bookmark. The title has had control and direction characters removed.</summary>
public sealed record OutlineEntry(string Title, Destination Destination, bool HasChildren);

/// <summary>One window of bookmarks at one level, and how many siblings that level has.</summary>
public sealed record OutlineWindow(int Total, IReadOnlyList<OutlineEntry> Entries);

/// <summary>A link annotation on a page. <see cref="Index"/> is its position in the page's links, which is how its address is asked for.</summary>
public sealed record PageLink(int Index, PdfRect Rect, Destination Destination);

public sealed record PageLinks(IReadOnlyList<PageLink> Links, bool Truncated);

public readonly record struct PageMatch(int Start, int Length);

public sealed record PageMatches(IReadOnlyList<PageMatch> Matches, bool Truncated);

public readonly record struct SearchOptions(string Query, bool MatchCase = false, bool WholeWord = false)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Query);

    internal uint Flags => (MatchCase ? 1u : 0u) | (WholeWord ? 2u : 0u);
}
