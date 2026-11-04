using System.Diagnostics;
using MikePDF.Protocol;

namespace MikePDF.Broker;

public enum EngineFaultKind
{
    /// <summary>The pipe ended or failed: the engine died or closed it.</summary>
    PipeClosed,

    /// <summary>The engine sent something it must never send. The supervisor kills it.</summary>
    ProtocolViolation,

    WriteFailed,

    /// <summary>The client was disposed by its owner.</summary>
    Disposed,
}

public sealed record EngineFault(EngineFaultKind Kind, string Detail);

public sealed record EngineClientOptions
{
    /// <summary>
    /// How long past a request's deadline the engine gets to answer before the request counts as <b>overdue</b>: the caller gets <c>Timeout</c> then.
    /// Overdue is not hung. PDFium looks at the deadline only about every 100 drawing operations, so a heavy page can overshoot it by seconds while the
    /// engine is working as it should. An overdue request makes the broker look at the engine's progress (see <see cref="StallTimeout"/>).
    /// </summary>
    public TimeSpan OverdueGrace { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// A request is overdue <i>and</i> the engine's progress counter (carried by every Pong) has not moved for this long: the engine thread is stuck,
    /// and the engine is reported hung. The counter moves whenever the engine thread starts or finishes a request or PDFium polls it for an interrupt
    /// (about every 100 drawing operations), so what this tolerates is the longest stretch of drawing between two polls, not the length of the call.
    /// A single drawing operation that takes longer than this cannot be told from a hang.
    /// </summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The upper bound per call: a request still unanswered this long after it became overdue is reported hung even though the engine keeps making
    /// progress (an endless loop that happens to poll). Measured from the end of the grace period.
    /// </summary>
    public TimeSpan HardCallLimit { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>How often overdue requests are looked at again. Pongs look too, so this only matters when none arrive (a client nobody pings).</summary>
    public TimeSpan LivenessInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Requests in flight that wait for the engine thread. Kept below the engine's own queue limit, which ends the process.</summary>
    public int MaxInFlightEngineRequests { get; init; } = 128;
}

/// <summary>
/// The broker's side of one engine connection: request/response correlation by requestId, deadlines, cancellation, and
/// validation of <b>every</b> response (A.9). The engine is untrusted. Anything it sends that does not fit a pending
/// request exactly (an unknown or repeated requestId, a different messageType, a status that message never has, a value
/// outside what was asked for) is a protocol violation: the client faults, every pending request ends with
/// <c>EngineCrashed</c>, and the supervisor kills the process.
/// </summary>
public sealed class EngineClient : IAsyncDisposable
{
    private const int MaxTileSide = 1024;

    private readonly Stream _pipe;
    private readonly SlotRing? _ring;
    private readonly EngineClientOptions _options;
    private readonly object _gate = new();
    private readonly Dictionary<ulong, Pending> _pending = [];
    private readonly Dictionary<uint, uint> _documents = []; // engine document id -> page count
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _inFlight;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _readLoop;
    private EngineFault? _fault;
    private long _nextRequestId;

    // Liveness (guarded by _gate). The engine's progress counter as the last Pong showed it, and when it last changed (or when a request reached an idle engine).
    private ulong _progress;
    private long _progressChangedAt = Stopwatch.GetTimestamp();
    private Timer? _liveness;
    private bool _overdueReported;

    /// <param name="pipe">The connected pipe to the peer.</param>
    /// <param name="ring">The slot ring tiles are rendered into; null for a peer that has none (the WriterHost).</param>
    /// <param name="options">Tuning.</param>
    public EngineClient(Stream pipe, SlotRing? ring, EngineClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        _pipe = pipe;
        _ring = ring;
        _options = options ?? new EngineClientOptions();
        _inFlight = new SemaphoreSlim(_options.MaxInFlightEngineRequests, int.MaxValue);
        _readLoop = Task.Run(ReadLoopAsync);
    }

    /// <summary>Raised once, from whichever thread noticed. Handlers must not block or throw.</summary>
    public event Action<EngineFault>? Faulted;

    /// <summary>
    /// Raised once when the engine must be treated as hung: a request that never reaches the engine thread (Ping, Hello, ...) was not answered in time, or a
    /// request that does is overdue and the engine has made no progress for <see cref="EngineClientOptions.StallTimeout"/> or has held it for
    /// <see cref="EngineClientOptions.HardCallLimit"/>. The request has already ended with <c>Timeout</c>.
    /// </summary>
    public event Action<MessageType>? RequestOverdue;

    public EngineFault? Fault
    {
        get
        {
            lock (_gate)
            {
                return _fault;
            }
        }
    }

    public bool IsFaulted => Fault is not null;

    // ---- typed calls ---------------------------------------------------------------------------

    public Task<EngineResult<HelloReply>> HelloAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
        CallAsync<HelloReply>(
            new Hello { SchemaVersion = ProtocolInfo.SchemaVersion, ProcessKind = ProcessKind.Broker, Pid = (uint)Environment.ProcessId },
            0,
            timeout,
            timeout,
            new Pending(),
            cancellationToken);

    public Task<EngineResult<Pong>> PingAsync(ulong nonce, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        CallAsync<Pong>(new Ping { Nonce = nonce }, 0, timeout, timeout, new Pending { PingNonce = nonce }, cancellationToken);

    /// <summary>
    /// Announces the slot ring (already duplicated into the peer, with write access) and its geometry. Sent once, right after Hello; the engine
    /// refuses RenderTile until it is attached.
    /// </summary>
    public Task<EngineResult<AttachSlotRingReply>> AttachSlotRingAsync(ulong remoteSectionHandle, int slotCount, int slotSize, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        CallAsync<AttachSlotRingReply>(
            new AttachSlotRing { Handle = remoteSectionHandle, SlotCount = (uint)slotCount, SlotSize = (uint)slotSize },
            0,
            timeout,
            timeout,
            new Pending(),
            cancellationToken);

    /// <summary>
    /// WriterHost: validates the structure of the PDF behind a file handle that was duplicated into the host. The host maps it read-only, parses it
    /// strictly and reports issues; the reply never carries file content beyond short messages.
    /// </summary>
    public Task<EngineResult<ValidateStructureReply>> ValidateStructureAsync(ulong remoteFileHandle, TimeSpan deadline, CancellationToken cancellationToken = default) =>
        CallAsync<ValidateStructureReply>(new ValidateStructure { Handle = remoteFileHandle }, 0, deadline, WithGrace(deadline), new Pending { EngineQueue = true }, cancellationToken);

    /// <summary>
    /// WriterHost, stage 4 of a save: appends the annotation edits in the edit blob to the base and writes base plus update to the output. The three
    /// values are file handles already duplicated into the host (base and edits read-only, output writable and empty). An <c>Ok</c> reply with
    /// <c>ok</c> false is a refusal with a reason; the broker validates the reply's counts against what it can know.
    /// </summary>
    public Task<EngineResult<AppendAnnotationsReply>> AppendAnnotationsAsync(ulong remoteBase, ulong remoteEdits, ulong editsLength, ulong remoteOutput, uint pageCount, TimeSpan deadline, CancellationToken cancellationToken = default) =>
        CallAsync<AppendAnnotationsReply>(
            new AppendAnnotations { BaseHandle = remoteBase, EditsHandle = remoteEdits, EditsLength = editsLength, OutputHandle = remoteOutput, PageCount = pageCount },
            0,
            deadline,
            WithGrace(deadline),
            new Pending { EngineQueue = true },
            cancellationToken);

    /// <summary>WriterHost: a full rewrite of the base into the output, optionally flattening form fields (flag 1) and comments (flag 2).</summary>
    public Task<EngineResult<RewriteDocumentReply>> RewriteDocumentAsync(ulong remoteBase, ulong remoteOutput, uint flags, TimeSpan deadline, CancellationToken cancellationToken = default) =>
        CallAsync<RewriteDocumentReply>(
            new RewriteDocument { BaseHandle = remoteBase, OutputHandle = remoteOutput, Flags = flags },
            0,
            deadline,
            WithGrace(deadline),
            new Pending { EngineQueue = true },
            cancellationToken);

    /// <summary>WriterHost: an independent strict parse of a finished output, comparing each page's annotation count with the base's plus the model's additions minus its removals.</summary>
    public Task<EngineResult<VerifyAnnotationsReply>> VerifyAnnotationsAsync(ulong remoteBase, ulong remoteOutput, uint pageCount, uint[] pages, uint[] added, uint[] removed, TimeSpan deadline, CancellationToken cancellationToken = default) =>
        CallAsync<VerifyAnnotationsReply>(
            new VerifyAnnotations { BaseHandle = remoteBase, OutputHandle = remoteOutput, PageCount = pageCount, Pages = pages, Added = added, Removed = removed },
            0,
            deadline,
            WithGrace(deadline),
            new Pending { EngineQueue = true },
            cancellationToken);

    public Task<EngineResult<ShutdownReply>> ShutdownAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
        CallAsync<ShutdownReply>(new Shutdown(), 0, timeout, timeout, new Pending(), cancellationToken);

    // remoteHandle: the file handle value as duplicated into the engine process.
    public Task<EngineResult<OpenDocumentReply>> OpenDocumentAsync(ulong remoteHandle, string? password, TimeSpan deadline, CancellationToken cancellationToken = default) =>
        CallAsync<OpenDocumentReply>(
            new OpenDocument { Handle = remoteHandle, Password = password },
            0,
            deadline,
            WithGrace(deadline),
            new Pending { EngineQueue = true },
            cancellationToken);

    public Task<EngineResult<CloseDocumentReply>> CloseDocumentAsync(uint documentId, TimeSpan deadline, CancellationToken cancellationToken = default) =>
        TryGetPageCount(documentId, out _)
            ? CallAsync<CloseDocumentReply>(new CloseDocument(), documentId, deadline, WithGrace(deadline), new Pending { EngineQueue = true }, cancellationToken)
            : Task.FromResult(EngineResult<CloseDocumentReply>.Fail(Status.InvalidArgument, "unknown document"));

    public Task<EngineResult<GetDocumentInfoReply>> GetDocumentInfoAsync(uint documentId, TimeSpan deadline, CancellationToken cancellationToken = default) =>
        TryGetPageCount(documentId, out _)
            ? CallAsync<GetDocumentInfoReply>(new GetDocumentInfo(), documentId, deadline, WithGrace(deadline), new Pending { EngineQueue = true }, cancellationToken)
            : Task.FromResult(EngineResult<GetDocumentInfoReply>.Fail(Status.InvalidArgument, "unknown document"));

    /// <summary>Dates, security, form type and fonts for the properties dialog (schema 4). The engine walks pages for the fonts, so this can take a second or two.</summary>
    public Task<EngineResult<GetDocumentDetailsReply>> GetDocumentDetailsAsync(uint documentId, TimeSpan deadline, CancellationToken cancellationToken = default) =>
        TryGetPageCount(documentId, out _)
            ? ReadAsync<GetDocumentDetailsReply>(new GetDocumentDetails(), documentId, deadline, cancellationToken)
            : Task.FromResult(EngineResult<GetDocumentDetailsReply>.Fail(Status.InvalidArgument, "unknown document"));

    public async Task<EngineResult<GetPageSizesReply>> GetPageSizesAsync(uint documentId, uint first, uint count, TimeSpan deadline, CancellationToken cancellationToken = default)
    {
        if (!TryGetPageCount(documentId, out var pageCount) || count == 0 || (ulong)first + count > pageCount)
        {
            return EngineResult<GetPageSizesReply>.Fail(Status.InvalidArgument, "unknown document or page range out of bounds");
        }

        var request = new GetPageSizes { First = first, Count = count };
        return await CallAsync<GetPageSizesReply>(request, documentId, deadline, WithGrace(deadline), new Pending { EngineQueue = true, Sizes = request }, cancellationToken).ConfigureAwait(false);
    }

    // ---- reading services (WP-S2). The engine's answers are checked against the request in ValidateReply: a window that does not fit what was asked,
    // a page outside the document or a reversed rectangle ends the session like any other protocol violation.

    public Task<EngineResult<GetOutlineReply>> GetOutlineAsync(uint documentId, IReadOnlyList<uint> path, uint first, uint count, TimeSpan deadline, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        return TryGetPageCount(documentId, out _)
            ? ReadAsync<GetOutlineReply>(new GetOutline { Path = [.. path], First = first, Count = count }, documentId, deadline, cancellationToken)
            : Task.FromResult(EngineResult<GetOutlineReply>.Fail(Status.InvalidArgument, "unknown document"));
    }

    public Task<EngineResult<GetPageLinksReply>> GetPageLinksAsync(uint documentId, uint page, uint first, uint count, TimeSpan deadline, CancellationToken cancellationToken = default) =>
        TryGetPageCount(documentId, out var pageCount) && page < pageCount
            ? ReadAsync<GetPageLinksReply>(new GetPageLinks { Page = page, First = first, Count = count }, documentId, deadline, cancellationToken)
            : Task.FromResult(EngineResult<GetPageLinksReply>.Fail(Status.InvalidArgument, "unknown document or page out of range"));

    public Task<EngineResult<GetLinkUriReply>> GetLinkUriAsync(uint documentId, uint page, uint index, TimeSpan deadline, CancellationToken cancellationToken = default) =>
        TryGetPageCount(documentId, out var pageCount) && page < pageCount
            ? ReadAsync<GetLinkUriReply>(new GetLinkUri { Page = page, Index = index }, documentId, deadline, cancellationToken)
            : Task.FromResult(EngineResult<GetLinkUriReply>.Fail(Status.InvalidArgument, "unknown document or page out of range"));

    public Task<EngineResult<GetPageBoxesReply>> GetPageBoxesAsync(uint documentId, uint page, TimeSpan deadline, CancellationToken cancellationToken = default) =>
        TryGetPageCount(documentId, out var pageCount) && page < pageCount
            ? ReadAsync<GetPageBoxesReply>(new GetPageBoxes { Page = page }, documentId, deadline, cancellationToken)
            : Task.FromResult(EngineResult<GetPageBoxesReply>.Fail(Status.InvalidArgument, "unknown document or page out of range"));

    public Task<EngineResult<GetPageTextReply>> GetPageTextAsync(uint documentId, uint page, uint first, uint count, TimeSpan deadline, CancellationToken cancellationToken = default) =>
        TryGetPageCount(documentId, out var pageCount) && page < pageCount
            ? ReadAsync<GetPageTextReply>(new GetPageText { Page = page, First = first, Count = count }, documentId, deadline, cancellationToken)
            : Task.FromResult(EngineResult<GetPageTextReply>.Fail(Status.InvalidArgument, "unknown document or page out of range"));

    public Task<EngineResult<SearchPageReply>> SearchPageAsync(uint documentId, uint page, string query, uint flags, TimeSpan deadline, CancellationToken cancellationToken = default) =>
        TryGetPageCount(documentId, out var pageCount) && page < pageCount
            ? ReadAsync<SearchPageReply>(new SearchPage { Page = page, Query = query, Flags = flags }, documentId, deadline, cancellationToken)
            : Task.FromResult(EngineResult<SearchPageReply>.Fail(Status.InvalidArgument, "unknown document or page out of range"));

    // ---- schema 7: text quads, the annotation read side, forms and the engine's save. Every call is checked against the document before it is sent, and every reply against the call
    // (ReplyChecks). A window that does not fit what was asked for, an index outside the page or a rectangle that is not one ends the session like any other protocol violation.

    /// <summary>
    /// The quadrilaterals of the characters <c>[start, start + count)</c> of a page (character numbering of GetPageText), one per line of text, in PDF user space and in the
    /// order top-left, top-right, bottom-left, bottom-right of the text's own frame. A reply holds 64 quads at most; ask again with <paramref name="firstQuad"/> moved on while
    /// <c>firstQuad + quads &lt; totalQuads</c>.
    /// </summary>
    public Task<EngineResult<GetTextQuadsReply>> GetTextQuadsAsync(uint documentId, uint page, uint start, uint count, uint firstQuad, TimeSpan deadline, CancellationToken cancellationToken = default) =>
        TryGetPageCount(documentId, out var pageCount) && page < pageCount && count > 0 && (ulong)start + count <= ReplyChecks.MaxPageCharacters
            ? ReadAsync<GetTextQuadsReply>(new GetTextQuads { Page = page, Start = start, Count = count, FirstQuad = firstQuad }, documentId, deadline, cancellationToken)
            : Task.FromResult(EngineResult<GetTextQuadsReply>.Fail(Status.InvalidArgument, "unknown document, page out of range or character range outside the page's text limit"));

    /// <summary>One window of up to 64 annotations of a page, in the order of the page's /Annots array (the index of an entry is its position there).</summary>
    public Task<EngineResult<ListAnnotationsReply>> ListAnnotationsAsync(uint documentId, uint page, uint first, uint count, TimeSpan deadline, CancellationToken cancellationToken = default) =>
        TryGetPageCount(documentId, out var pageCount) && page < pageCount
            ? ReadAsync<ListAnnotationsReply>(new ListAnnotations { Page = page, First = first, Count = count }, documentId, deadline, cancellationToken)
            : Task.FromResult(EngineResult<ListAnnotationsReply>.Fail(Status.InvalidArgument, "unknown document or page out of range"));

    public Task<EngineResult<GetAnnotationReply>> GetAnnotationAsync(uint documentId, uint page, uint index, TimeSpan deadline, CancellationToken cancellationToken = default) =>
        TryGetPageCount(documentId, out var pageCount) && page < pageCount
            ? ReadAsync<GetAnnotationReply>(new GetAnnotation { Page = page, Index = index }, documentId, deadline, cancellationToken)
            : Task.FromResult(EngineResult<GetAnnotationReply>.Fail(Status.InvalidArgument, "unknown document or page out of range"));

    /// <summary>A window of up to 1024 points of one stroke of an ink annotation.</summary>
    public Task<EngineResult<GetInkStrokeReply>> GetInkStrokeAsync(uint documentId, uint page, uint index, uint stroke, uint first, uint count, TimeSpan deadline, CancellationToken cancellationToken = default) =>
        TryGetPageCount(documentId, out var pageCount) && page < pageCount
            ? ReadAsync<GetInkStrokeReply>(new GetInkStroke { Page = page, Index = index, Stroke = stroke, First = first, Count = count }, documentId, deadline, cancellationToken)
            : Task.FromResult(EngineResult<GetInkStrokeReply>.Fail(Status.InvalidArgument, "unknown document or page out of range"));

    /// <summary>
    /// The display mirror (annotation-core 3.3): hides or shows one annotation in the engine's in-memory document so that tiles stop drawing an original the user deleted or moved.
    /// The engine puts the file's own flags back before it saves.
    /// </summary>
    public Task<EngineResult<SetAnnotationHiddenReply>> SetAnnotationHiddenAsync(uint documentId, uint page, uint index, bool hidden, TimeSpan deadline, CancellationToken cancellationToken = default) =>
        TryGetPageCount(documentId, out var pageCount) && page < pageCount
            ? ReadAsync<SetAnnotationHiddenReply>(new SetAnnotationHidden { Page = page, Index = index, Hidden = hidden }, documentId, deadline, cancellationToken)
            : Task.FromResult(EngineResult<SetAnnotationHiddenReply>.Fail(Status.InvalidArgument, "unknown document or page out of range"));

    /// <summary>How many annotations each of <paramref name="count"/> pages from <paramref name="firstPage"/> has that a person sees (not links, popups or widgets).</summary>
    public Task<EngineResult<CountAnnotationsReply>> CountAnnotationsAsync(uint documentId, uint firstPage, uint count, TimeSpan deadline, CancellationToken cancellationToken = default) =>
        TryGetPageCount(documentId, out var pageCount) && count > 0 && (ulong)firstPage + count <= pageCount
            ? ReadAsync<CountAnnotationsReply>(new CountAnnotations { FirstPage = firstPage, Count = count }, documentId, deadline, cancellationToken)
            : Task.FromResult(EngineResult<CountAnnotationsReply>.Fail(Status.InvalidArgument, "unknown document or page range out of bounds"));

    /// <summary>One window of up to 64 form widgets in tab order. The first call walks the document's pages, so it can take a while on a big form.</summary>
    public Task<EngineResult<GetFormFieldsReply>> GetFormFieldsAsync(uint documentId, uint first, uint count, TimeSpan deadline, CancellationToken cancellationToken = default) =>
        TryGetPageCount(documentId, out _)
            ? ReadAsync<GetFormFieldsReply>(new GetFormFields { First = first, Count = count }, documentId, deadline, cancellationToken)
            : Task.FromResult(EngineResult<GetFormFieldsReply>.Fail(Status.InvalidArgument, "unknown document"));

    public Task<EngineResult<GetFormFieldDetailReply>> GetFormFieldDetailAsync(uint documentId, uint fieldId, uint firstOption, TimeSpan deadline, CancellationToken cancellationToken = default) =>
        TryGetPageCount(documentId, out _)
            ? ReadAsync<GetFormFieldDetailReply>(new GetFormFieldDetail { FieldId = fieldId, FirstOption = firstOption }, documentId, deadline, cancellationToken)
            : Task.FromResult(EngineResult<GetFormFieldDetailReply>.Fail(Status.InvalidArgument, "unknown document"));

    /// <summary>
    /// Changes one field's value through PDFium's form-fill events. <paramref name="kind"/> 0 is text (<paramref name="text"/>), 1 a toggle (<paramref name="isChecked"/>), 2 a choice
    /// (<paramref name="selected"/>, ascending option positions). The reply says whether the field now holds what was asked for, what it holds, and which pages to redraw.
    /// </summary>
    public Task<EngineResult<SetFormFieldValueReply>> SetFormFieldValueAsync(uint documentId, uint fieldId, ushort kind, string? text, bool isChecked, IReadOnlyList<uint>? selected, TimeSpan deadline, CancellationToken cancellationToken = default) =>
        TryGetPageCount(documentId, out _)
            ? ReadAsync<SetFormFieldValueReply>(new SetFormFieldValue { FieldId = fieldId, Kind = kind, Text = text ?? string.Empty, Checked = isChecked, Selected = selected is null ? [] : [.. selected] }, documentId, deadline, cancellationToken)
            : Task.FromResult(EngineResult<SetFormFieldValueReply>.Fail(Status.InvalidArgument, "unknown document"));

    /// <summary>Resets a field (0), resets every field (1, <paramref name="fieldId"/> is ignored: send 0) or presses a push button (2). Only a reset is ever performed.</summary>
    public Task<EngineResult<FormActionReply>> FormActionAsync(uint documentId, uint fieldId, ushort action, TimeSpan deadline, CancellationToken cancellationToken = default) =>
        TryGetPageCount(documentId, out _)
            ? ReadAsync<FormActionReply>(new FormAction { FieldId = fieldId, Action = action }, documentId, deadline, cancellationToken)
            : Task.FromResult(EngineResult<FormActionReply>.Fail(Status.InvalidArgument, "unknown document"));

    /// <summary>
    /// PDFium's own save of the engine's in-memory document into a file handle already duplicated into the engine (write access, empty file): mode 0 incremental, 1 full rewrite.
    /// Mode 2 and the flags belong to a flattened copy, which the writer host makes, and the engine refuses them. The engine consumes the handle on every path.
    /// </summary>
    public Task<EngineResult<SaveDocumentReply>> SaveDocumentAsync(uint documentId, ushort mode, uint flags, ulong remoteOutput, TimeSpan deadline, CancellationToken cancellationToken = default) =>
        TryGetPageCount(documentId, out _)
            ? CallAsync<SaveDocumentReply>(new SaveDocument { Mode = mode, Flags = flags, OutputHandle = remoteOutput }, documentId, deadline, WithGrace(deadline), new Pending { EngineQueue = true }, cancellationToken)
            : Task.FromResult(EngineResult<SaveDocumentReply>.Fail(Status.InvalidArgument, "unknown document"));

    /// <summary>
    /// WriterHost, stage 3 of a save: repairs the tail PDFium appended to a file. The three handles are already duplicated into the host (original and PDFium's file read-only,
    /// output writable and empty). An <c>Ok</c> reply with <c>ok</c> false is a refusal with a reason.
    /// </summary>
    public Task<EngineResult<NormalizeTailReply>> NormalizeTailAsync(ulong remoteOriginal, ulong remotePdfium, ulong remoteOutput, uint flags, TimeSpan deadline, CancellationToken cancellationToken = default) =>
        CallAsync<NormalizeTailReply>(
            new NormalizeTail { OriginalHandle = remoteOriginal, PdfiumHandle = remotePdfium, OutputHandle = remoteOutput, Flags = flags },
            0,
            deadline,
            WithGrace(deadline),
            new Pending { EngineQueue = true },
            cancellationToken);

    private Task<EngineResult<T>> ReadAsync<T>(IRequestMessage request, uint documentId, TimeSpan deadline, CancellationToken cancellationToken)
        where T : class =>
        CallAsync<T>(request, documentId, deadline, WithGrace(deadline), new Pending { EngineQueue = true, Request = request }, cancellationToken);

    /// <summary>The document's /ViewerPreferences (schema 5): reading direction, print defaults, page ranges. Every value is bounded; the ranges are checked against the page count.</summary>
    public Task<EngineResult<GetViewerPreferencesReply>> GetViewerPreferencesAsync(uint documentId, TimeSpan deadline, CancellationToken cancellationToken = default) =>
        TryGetPageCount(documentId, out _)
            ? ReadAsync<GetViewerPreferencesReply>(new GetViewerPreferences(), documentId, deadline, cancellationToken)
            : Task.FromResult(EngineResult<GetViewerPreferencesReply>.Fail(Status.InvalidArgument, "unknown document"));

    /// <summary>
    /// Renders rows <c>BandTop</c> to <c>BandTop + BandHeight</c> of a page laid out at <c>PageWidthPx</c> x <c>PageHeightPx</c> device pixels into a slot (schema 5,
    /// printing). The band is checked against the ring before it is sent (it has to fit a slot) and the reply against the request: width, height, stride and slot
    /// must be what was asked for. Cancelling and <paramref name="generation"/> work as for <see cref="RenderTileAsync"/>. The tile lease it returns is the band.
    /// </summary>
    public async Task<EngineResult<TileLease>> RenderBandAsync(uint documentId, RenderBandArgs args, ulong generation, TimeSpan deadline, CancellationToken cancellationToken = default)
    {
        if (_ring is null)
        {
            return EngineResult<TileLease>.Fail(Status.InvalidArgument, "this peer has no slot ring");
        }

        if (!TryGetPageCount(documentId, out var pageCount) || args.Page >= pageCount)
        {
            return EngineResult<TileLease>.Fail(Status.InvalidArgument, "unknown document or page out of range");
        }

        if (args.Dpi is < 72 or > 1200 || args.PageWidthPx is < 1 or > 65536 || args.PageHeightPx is < 1 or > 65536 || args.BandHeight is < 1 or > 16384 || (ulong)args.BandTop + args.BandHeight > args.PageHeightPx
            || args.Flags > 7 || args.Rotation > 3)
        {
            return EngineResult<TileLease>.Fail(Status.InvalidArgument, "the band is outside the page or the limits of the protocol");
        }

        if ((ulong)args.PageWidthPx * 4 * args.BandHeight > (ulong)_ring.SlotSize)
        {
            return EngineResult<TileLease>.Fail(Status.InvalidArgument, "the band does not fit a slot");
        }

        SlotLease lease;
        try
        {
            lease = await _ring.RentAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return EngineResult<TileLease>.Fail(Status.Cancelled);
        }

        var request = new RenderBand
        {
            Page = args.Page,
            Dpi = args.Dpi,
            Rotation = args.Rotation,
            PageWidthPx = args.PageWidthPx,
            PageHeightPx = args.PageHeightPx,
            BandTop = args.BandTop,
            BandHeight = args.BandHeight,
            Flags = args.Flags,
            SlotIndex = (uint)lease.Index,
            Generation = generation,
        };
        var pending = new Pending { EngineQueue = true, Band = request, Slot = lease };
        if (generation < ulong.MaxValue)
        {
            pending.OnCancel = () => _ = CancelBeforeAsync(documentId, generation + 1, TimeSpan.FromSeconds(2));
        }

        return await CallAsync<TileLease>(request, documentId, deadline, WithGrace(deadline), pending, cancellationToken).ConfigureAwait(false);
    }

    public Task<EngineResult<CancelBeforeReply>> CancelBeforeAsync(uint documentId, ulong generation, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        CallAsync<CancelBeforeReply>(new CancelBefore { Generation = generation }, documentId, timeout, timeout, new Pending(), cancellationToken);

    /// <summary>
    /// Renders one tile into a slot of the ring. <paramref name="generation"/> is the caller's epoch: cancelling
    /// <paramref name="cancellationToken"/> ends this call with <c>Cancelled</c> at once and sends <c>CancelBefore(generation + 1)</c>,
    /// which cancels this and every older render of the document that has not finished.
    /// </summary>
    public async Task<EngineResult<TileLease>> RenderTileAsync(uint documentId, RenderTileArgs args, ulong generation, TimeSpan deadline, CancellationToken cancellationToken = default)
    {
        if (_ring is null)
        {
            return EngineResult<TileLease>.Fail(Status.InvalidArgument, "this peer has no slot ring");
        }

        if (!TryGetPageCount(documentId, out var pageCount) || args.Page >= pageCount)
        {
            return EngineResult<TileLease>.Fail(Status.InvalidArgument, "unknown document or page out of range");
        }

        if (args.TileWidth is < 1 or > MaxTileSide || args.TileHeight is < 1 or > MaxTileSide || (long)args.TileWidth * 4 * args.TileHeight > _ring.SlotSize)
        {
            return EngineResult<TileLease>.Fail(Status.InvalidArgument, "the tile does not fit a slot");
        }

        SlotLease lease;
        try
        {
            lease = await _ring.RentAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return EngineResult<TileLease>.Fail(Status.Cancelled);
        }

        var request = new RenderTile
        {
            Page = args.Page,
            Scale = args.Scale,
            Rotation = args.Rotation,
            TileX = args.TileX,
            TileY = args.TileY,
            TileWidth = args.TileWidth,
            TileHeight = args.TileHeight,
            Flags = args.Flags,
            SlotIndex = (uint)lease.Index,
            Generation = generation,
        };
        var pending = new Pending { EngineQueue = true, Render = request, Slot = lease };
        if (generation < ulong.MaxValue)
        {
            pending.OnCancel = () => _ = CancelBeforeAsync(documentId, generation + 1, TimeSpan.FromSeconds(2));
        }

        return await CallAsync<TileLease>(request, documentId, deadline, WithGrace(deadline), pending, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        FaultWith(EngineFaultKind.Disposed, "The client was disposed.");
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _readLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
    }

    // ---- sending -------------------------------------------------------------------------------

    private TimeSpan WithGrace(TimeSpan deadline) => deadline <= TimeSpan.Zero ? Timeout.InfiniteTimeSpan : deadline + _options.OverdueGrace;

    private bool TryGetPageCount(uint documentId, out uint pageCount)
    {
        lock (_gate)
        {
            return _documents.TryGetValue(documentId, out pageCount);
        }
    }

    private async Task<EngineResult<T>> CallAsync<T>(IRequestMessage payload, uint documentId, TimeSpan deadline, TimeSpan localTimeout, Pending pending, CancellationToken cancellationToken)
        where T : class
    {
        var outcome = await SendAsync(payload, documentId, deadline, localTimeout, pending, cancellationToken).ConfigureAwait(false);
        if (outcome.Tile is not null && typeof(T) == typeof(TileLease))
        {
            return new EngineResult<T>(outcome.Status, (T)(object)outcome.Tile, outcome.Detail);
        }

        // The dispatcher only delivers the reply type that belongs to the pending message type, so this cast cannot fail on engine input.
        return new EngineResult<T>(outcome.Status, outcome.Reply as T, outcome.Detail);
    }

    private async Task<Outcome> SendAsync(IRequestMessage payload, uint documentId, TimeSpan deadline, TimeSpan localTimeout, Pending pending, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            pending.Slot?.Dispose();
            return new Outcome(Status.Cancelled, null, null, null);
        }

        var inFlightTaken = false;
        if (pending.EngineQueue)
        {
            try
            {
                await _inFlight.WaitAsync(cancellationToken).ConfigureAwait(false);
                inFlightTaken = true;
            }
            catch (OperationCanceledException)
            {
                pending.Slot?.Dispose();
                return new Outcome(Status.Cancelled, null, null, null);
            }
        }

        pending.InFlightTaken = inFlightTaken;
        pending.Type = payload.MessageType;
        pending.DocumentId = documentId;
        var requestId = (ulong)Interlocked.Increment(ref _nextRequestId);
        pending.Id = requestId;

        var frame = ProtocolCodec.EncodeRequestFrame(requestId, documentId, ToMilliseconds(deadline), payload);
        if (!frame.IsOk)
        {
            ReleaseUnsent(pending);
            return new Outcome(Status.InvalidArgument, null, null, frame.Failure.Detail);
        }

        lock (_gate)
        {
            if (_fault is not null)
            {
                ReleaseUnsent(pending);
                return new Outcome(Status.EngineCrashed, null, null, _fault.Detail);
            }

            if (pending.EngineQueue && !_pending.Values.Any(p => p.EngineQueue))
            {
                // The engine thread was idle and is about to be given work: an idle counter standing still is not a stall, so the clock starts now.
                _progressChangedAt = Stopwatch.GetTimestamp();
            }

            _pending.Add(requestId, pending);
        }

        if (localTimeout > TimeSpan.Zero && localTimeout != Timeout.InfiniteTimeSpan)
        {
            pending.Timer = new Timer(_ => Abandon(pending, Status.Timeout, "The engine did not answer in time.", overdue: true), null, localTimeout, Timeout.InfiniteTimeSpan);
        }

        pending.CancellationRegistration = cancellationToken.Register(() =>
        {
            if (Abandon(pending, Status.Cancelled, null, overdue: false))
            {
                pending.OnCancel?.Invoke();
            }
        });

        if (pending.Completion.Task.IsCompleted)
        {
            // Answered, cancelled or faulted while the timer and registration were being set up: they have nothing left to watch.
            pending.Timer?.Dispose();
            pending.CancellationRegistration.Dispose();
        }

        try
        {
            await _writeLock.WaitAsync(_stop.Token).ConfigureAwait(false);
            try
            {
                await _pipe.WriteAsync(frame.Value, _stop.Token).ConfigureAwait(false);
                await _pipe.FlushAsync(_stop.Token).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            FaultWith(EngineFaultKind.WriteFailed, "Writing to the engine failed: " + e.Message);
        }

        return await pending.Completion.Task.ConfigureAwait(false);
    }

    private static uint ToMilliseconds(TimeSpan deadline)
    {
        if (deadline <= TimeSpan.Zero)
        {
            return 0; // no deadline
        }

        var ms = Math.Ceiling(deadline.TotalMilliseconds);
        return ms >= ProtocolInfo.MaxDeadlineMs ? ProtocolInfo.MaxDeadlineMs : Math.Max(1u, (uint)ms);
    }

    /// <summary>Ends a pending request locally. The entry stays in the table until the engine answers, so its answer is still matched.</summary>
    private bool Abandon(Pending pending, Status status, string? detail, bool overdue)
    {
        lock (_gate)
        {
            if (!_pending.ContainsKey(pending.Id) || pending.Abandoned)
            {
                return false;
            }

            pending.Abandoned = true;
            if (overdue)
            {
                pending.OverdueSince = Stopwatch.GetTimestamp();
            }
        }

        pending.Completion.TrySetResult(new Outcome(status, null, null, detail));
        if (overdue)
        {
            if (pending.EngineQueue)
            {
                // Overdue is not hung: the engine may be working through a heavy page. Whether it is stuck is decided from its progress.
                WatchLiveness();
            }
            else
            {
                // A request the I/O thread answers by itself (Ping, Hello, ...) has no excuse: that thread is not responding.
                Notify(() => RequestOverdue?.Invoke(pending.Type));
            }
        }

        return true;
    }

    // ---- liveness: busy or stuck (W2-engine item 2) -----------------------------------------------

    /// <summary>Starts looking at the overdue requests: now, on every Pong, and on a timer for as long as one is overdue.</summary>
    private void WatchLiveness()
    {
        lock (_gate)
        {
            if (_fault is null && _liveness is null)
            {
                _liveness = new Timer(_ => EvaluateLiveness(), null, _options.LivenessInterval, _options.LivenessInterval);
            }
        }

        EvaluateLiveness();
    }

    /// <summary>
    /// The engine is hung when a request is overdue and either its progress counter has stood still for the stall timeout (the engine thread is stuck, the
    /// I/O thread still answering pings) or the request has been overdue for the hard limit (an engine that polls forever). A request that is merely slow, with
    /// the counter moving, is not.
    /// </summary>
    private void EvaluateLiveness()
    {
        MessageType type = default;
        var hung = false;
        lock (_gate)
        {
            if (_fault is not null || _overdueReported)
            {
                return;
            }

            var now = Stopwatch.GetTimestamp();
            var stalled = Stopwatch.GetElapsedTime(_progressChangedAt, now) >= _options.StallTimeout;
            var anyOverdue = false;
            foreach (var pending in _pending.Values)
            {
                if (pending.OverdueSince == 0)
                {
                    continue;
                }

                anyOverdue = true;
                if (stalled || Stopwatch.GetElapsedTime(pending.OverdueSince, now) >= _options.HardCallLimit)
                {
                    hung = true;
                    type = pending.Type;
                    break;
                }
            }

            if (!anyOverdue)
            {
                _liveness?.Dispose();
                _liveness = null;
                return;
            }

            _overdueReported = hung;
        }

        if (hung)
        {
            Notify(() => RequestOverdue?.Invoke(type));
        }
    }

    /// <summary>A Pong's progress counter. A different value than last time (it counts up and wraps) is progress. Gate held.</summary>
    private void ObserveProgress(ulong progress)
    {
        if (progress != _progress)
        {
            _progress = progress;
            _progressChangedAt = Stopwatch.GetTimestamp();
        }
    }

    private void ReleaseUnsent(Pending pending)
    {
        pending.Slot?.Dispose();
        if (pending.InFlightTaken)
        {
            _inFlight.Release();
        }
    }

    // ---- receiving -----------------------------------------------------------------------------

    private async Task ReadLoopAsync()
    {
        var header = new byte[ProtocolCodec.FrameHeaderBytes];
        try
        {
            while (true)
            {
                await _pipe.ReadExactlyAsync(header, _stop.Token).ConfigureAwait(false);
                if (ProtocolCodec.ReadFrameLength(header, out var length) is { } badLength)
                {
                    Violation("bad frame length: " + badLength.Detail);
                    return;
                }

                var body = new byte[length];
                await _pipe.ReadExactlyAsync(body, _stop.Token).ConfigureAwait(false);
                var decoded = ProtocolCodec.DecodeResponseBody(body);
                if (!decoded.IsOk)
                {
                    Violation("malformed response: " + decoded.Failure.Detail);
                    return;
                }

                if (!Dispatch(decoded.Value))
                {
                    return;
                }
            }
        }
        catch (Exception e) when (e is EndOfStreamException or IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            FaultWith(EngineFaultKind.PipeClosed, "The engine closed the pipe.");
        }
    }

    private void Violation(string detail) => FaultWith(EngineFaultKind.ProtocolViolation, detail);

    /// <summary>Returns false after a violation (the client has faulted).</summary>
    private bool Dispatch(ResponseEnvelope response)
    {
        Pending? pending;
        Outcome outcome = default;
        string? violation = null;
        lock (_gate)
        {
            if (!_pending.Remove(response.RequestId, out pending))
            {
                violation = "response for a requestId that is not pending (unknown or repeated)";
            }
            else if (response.MessageType != pending.Type)
            {
                violation = $"response messageType {response.MessageType} does not echo the request ({pending.Type})";
            }
            else if (!IsStatusAllowed(pending.Type, response.Status))
            {
                violation = $"status {response.Status} is not a valid answer to {pending.Type}";
            }
            else if ((response.Payload is null ? null : ValidateReply(pending, response.Payload)) is { } invalid)
            {
                violation = invalid;
            }
            else
            {
                outcome = BuildOutcome(pending, response);
            }
        }

        if (violation is not null)
        {
            if (pending is not null)
            {
                pending.Timer?.Dispose();
                pending.CancellationRegistration.Dispose();
                pending.Slot?.Dispose();
                pending.Completion.TrySetResult(new Outcome(Status.EngineCrashed, null, null, violation));
            }

            Violation(violation);
            return false;
        }

        pending!.Timer?.Dispose();
        pending.CancellationRegistration.Dispose();
        if (pending.InFlightTaken)
        {
            _inFlight.Release();
        }

        if (!pending.Completion.TrySetResult(outcome))
        {
            // The caller gave up first (cancelled or timed out): hand the resources back.
            outcome.Tile?.Dispose();
            if (response.Payload is OpenDocumentReply abandoned)
            {
                _ = CloseDocumentAsync(abandoned.DocumentId, TimeSpan.FromSeconds(5));
            }
        }

        pending.Slot?.Dispose();
        if (response.Payload is Pong)
        {
            EvaluateLiveness(); // the counter in this Pong may be the evidence that an overdue request is, or is not, a hang
        }

        return true;
    }

    private static bool IsStatusAllowed(MessageType type, Status status) => type switch
    {
        MessageType.Hello => status is Status.Ok or Status.Unsupported,
        MessageType.Ping or MessageType.Shutdown => status is Status.Ok,
        MessageType.CancelBefore or MessageType.CloseDocument or MessageType.AttachSlotRing => status is Status.Ok or Status.InvalidArgument,
        MessageType.ValidateStructure => status is Status.Ok or Status.InvalidArgument or Status.Damaged or Status.Timeout or Status.PermissionDenied or Status.Unsupported,
        MessageType.AppendAnnotations or MessageType.RewriteDocument or MessageType.VerifyAnnotations or MessageType.NormalizeTail
            => status is Status.Ok or Status.InvalidArgument or Status.Unsupported or Status.Timeout or Status.PermissionDenied,
        MessageType.GetTextQuads or MessageType.ListAnnotations or MessageType.GetAnnotation or MessageType.CountAnnotations or MessageType.SetAnnotationHidden
            => status is Status.Ok or Status.InvalidArgument or Status.Damaged or Status.Timeout,
        MessageType.GetInkStroke => status is Status.Ok or Status.InvalidArgument or Status.Unsupported or Status.Damaged or Status.Timeout,
        MessageType.GetFormFields or MessageType.GetFormFieldDetail => status is Status.Ok or Status.InvalidArgument or Status.Unsupported or Status.Damaged or Status.Timeout,
        MessageType.SetFormFieldValue or MessageType.FormAction => status is Status.Ok or Status.InvalidArgument or Status.Unsupported or Status.Damaged or Status.Timeout or Status.PermissionDenied,
        MessageType.SaveDocument => status is Status.Ok or Status.InvalidArgument or Status.Unsupported or Status.Damaged or Status.Timeout or Status.PermissionDenied,
        MessageType.OpenDocument => status is Status.Ok or Status.InvalidArgument or Status.Unsupported or Status.PasswordRequired
            or Status.PasswordIncorrect or Status.UnsupportedEncryption or Status.Damaged or Status.Timeout or Status.PermissionDenied,
        MessageType.GetDocumentInfo or MessageType.GetPageSizes or MessageType.GetOutline or MessageType.GetPageLinks or MessageType.GetPageBoxes
            or MessageType.GetPageText or MessageType.SearchPage or MessageType.GetDocumentDetails or MessageType.GetViewerPreferences
            => status is Status.Ok or Status.InvalidArgument or Status.Damaged or Status.Timeout,
        MessageType.GetLinkUri => status is Status.Ok or Status.InvalidArgument or Status.Unsupported or Status.Damaged or Status.Timeout,
        MessageType.RenderTile or MessageType.RenderBand => status is Status.Ok or Status.PartiallyRendered or Status.Cancelled or Status.InvalidArgument
            or Status.Unsupported or Status.Damaged or Status.Timeout,
        _ => false, // EngineCrashed and ProtocolError are never the peer's to send
    };

    /// <summary>Semantic checks of a decoded reply against the request it answers (the schema limits were enforced by the decoder). Gate held.</summary>
    private string? ValidateReply(Pending pending, IReplyMessage reply)
    {
        switch (reply)
        {
            case HelloReply hello:
                return hello.SchemaVersion != ProtocolInfo.SchemaVersion ? "the engine speaks another schema version" : null;
            case Pong pong:
                return pong.Nonce != pending.PingNonce ? "pong nonce does not match the ping" : null;
            case OpenDocumentReply opened:
                return _documents.ContainsKey(opened.DocumentId) ? "the engine reused a document id" : null;
            case GetDocumentInfoReply info:
                return !_documents.TryGetValue(pending.DocumentId, out var count) || info.PageCount != count ? "the page count changed" : null;
            case GetPageSizesReply sizes:
                return pending.Sizes is { } asked && sizes.Widths.Length == asked.Count ? null : "wrong number of page sizes";
            case RenderTileReply tile:
                return ValidateTile(pending, tile);
            case RenderBandReply band:
                return ValidateBand(pending, band);
            case GetViewerPreferencesReply preferences:
                return _documents.TryGetValue(pending.DocumentId, out var documentPages) ? ReplyChecks.CheckPreferences(preferences, documentPages) : "preferences for an unknown document";
            case GetOutlineReply outline:
                return ValidateOutline(pending, outline);
            case GetPageLinksReply links:
                return ValidateLinks(pending, links);
            case GetLinkUriReply uri:
                return ReplyChecks.IsPlainAddress(uri.Uri) ? null : "the link address holds characters an address never has";
            case GetPageTextReply text:
                return ValidateText(pending, text);
            case SearchPageReply search:
                return ReplyChecks.CheckMatches(search.Starts, search.Lengths);
            case AppendAnnotationsReply append:
                return ReplyChecks.CheckSaveReply(append.Ok, append.IssueCodes.Length, append.BytesWritten);
            case RewriteDocumentReply rewrite:
                return ReplyChecks.CheckSaveReply(rewrite.Ok, rewrite.IssueCodes.Length, rewrite.BytesWritten);
            case VerifyAnnotationsReply verify:
                return !verify.Ok && verify.Mismatches.Length > 0 ? "a failed verification names mismatched pages" : null;
            case GetDocumentDetailsReply details:
                return ReplyChecks.CheckDetails(details, _documents.TryGetValue(pending.DocumentId, out var pages) ? pages : 0);
            case GetTextQuadsReply quads:
                return pending.Request is GetTextQuads quadsAsked ? ReplyChecks.CheckTextQuads(quadsAsked, quads) : "text quads without a text quad request";
            case ListAnnotationsReply annotations:
                return pending.Request is ListAnnotations listAsked ? ReplyChecks.CheckAnnotationList(listAsked, annotations) : "an annotation list without a list request";
            case GetAnnotationReply annotation:
                return pending.Request is GetAnnotation annotationAsked ? ReplyChecks.CheckAnnotation(annotationAsked, annotation) : "an annotation without an annotation request";
            case GetInkStrokeReply stroke:
                return pending.Request is GetInkStroke strokeAsked ? ReplyChecks.CheckInkStroke(strokeAsked, stroke) : "a stroke without a stroke request";
            case CountAnnotationsReply counts:
                return pending.Request is CountAnnotations countAsked ? ReplyChecks.CheckAnnotationCounts(countAsked, counts) : "annotation counts without a count request";
            case GetFormFieldsReply fields:
                return pending.Request is GetFormFields fieldsAsked && _documents.TryGetValue(pending.DocumentId, out var fieldPages)
                    ? ReplyChecks.CheckFormFields(fieldsAsked, fields, fieldPages)
                    : "form fields without a form field request";
            case GetFormFieldDetailReply detail:
                return pending.Request is GetFormFieldDetail detailAsked ? ReplyChecks.CheckFormFieldDetail(detailAsked, detail) : "a field detail without a detail request";
            case SetFormFieldValueReply setValue:
                return _documents.TryGetValue(pending.DocumentId, out var setPages) ? ReplyChecks.CheckSetFormFieldValue(setValue, setPages) : "a field value for an unknown document";
            case FormActionReply formAction:
                return pending.Request is FormAction actionAsked && _documents.TryGetValue(pending.DocumentId, out var actionPages)
                    ? ReplyChecks.CheckFormAction(actionAsked, formAction, actionPages)
                    : "a form action reply without a form action request";
            case SaveDocumentReply saved:
                return _documents.TryGetValue(pending.DocumentId, out var savedPages) ? ReplyChecks.CheckSaveDocument(saved, savedPages) : "a save of an unknown document";
            case NormalizeTailReply normalize:
                return ReplyChecks.CheckSaveReply(normalize.Ok, normalize.IssueCodes.Length, normalize.BytesWritten);
            default:
                return null;
        }
    }

    private string? ValidateOutline(Pending pending, GetOutlineReply reply)
    {
        if (pending.Request is not GetOutline asked || !_documents.TryGetValue(pending.DocumentId, out var pageCount))
        {
            return "outline reply without an outline request";
        }

        return !ReplyChecks.WindowFits(asked.First, asked.Count, reply.Total, reply.Titles.Length, allowStartPastEnd: true)
            ? "the outline window is not what was asked for"
            : ReplyChecks.CheckDestinations(reply.Kinds, reply.Pages, pageCount);
    }

    private string? ValidateLinks(Pending pending, GetPageLinksReply reply)
    {
        if (pending.Request is not GetPageLinks asked || !_documents.TryGetValue(pending.DocumentId, out var pageCount))
        {
            return "link reply without a link request";
        }

        if (!ReplyChecks.WindowFits(asked.First, asked.Count, reply.Total, reply.Lefts.Length, allowStartPastEnd: true))
        {
            return "the link window is not what was asked for";
        }

        for (var i = 0; i < reply.Lefts.Length; i++)
        {
            if (reply.Lefts[i] > reply.Rights[i] || reply.Bottoms[i] > reply.Tops[i])
            {
                return "a link rectangle is reversed";
            }
        }

        return ReplyChecks.CheckDestinations(reply.Kinds, reply.Pages, pageCount);
    }

    private static string? ValidateText(Pending pending, GetPageTextReply reply) =>
        pending.Request is not GetPageText asked || !ReplyChecks.WindowFits(asked.First, asked.Count, reply.Total, reply.Units.Length, allowStartPastEnd: false)
            ? "the text window is not what was asked for"
            : null;

    private string? ValidateTile(Pending pending, RenderTileReply tile)
    {
        if (_ring is null || pending.Render is not { } asked || pending.Slot is not { } slot)
        {
            return "tile reply without a render request";
        }

        if (tile.SlotIndex != asked.SlotIndex || tile.SlotIndex >= (uint)_ring.SlotCount || tile.SlotIndex != (uint)slot.Index)
        {
            return "tile reply names a slot that was not requested";
        }

        if (tile.Width != asked.TileWidth || tile.Height != asked.TileHeight)
        {
            return "tile dimensions differ from the request";
        }

        if ((ulong)tile.Stride < (ulong)tile.Width * 4)
        {
            return "tile stride is smaller than a row";
        }

        if ((ulong)tile.Stride * tile.Height > (ulong)_ring.SlotSize)
        {
            return "tile does not fit in its slot";
        }

        return tile.Generation != asked.Generation ? "tile generation differs from the request" : null;
    }

    private string? ValidateBand(Pending pending, RenderBandReply band)
    {
        if (_ring is null || pending.Band is not { } asked || pending.Slot is not { } slot)
        {
            return "band reply without a band request";
        }

        if (band.SlotIndex != asked.SlotIndex || band.SlotIndex >= (uint)_ring.SlotCount || band.SlotIndex != (uint)slot.Index)
        {
            return "band reply names a slot that was not requested";
        }

        if (band.Width != asked.PageWidthPx || band.Height != asked.BandHeight)
        {
            return "band dimensions differ from the request";
        }

        if (band.Stride != band.Width * 4UL)
        {
            return "band stride is not four bytes a pixel";
        }

        if ((ulong)band.Stride * band.Height > (ulong)_ring.SlotSize)
        {
            return "band does not fit in its slot";
        }

        return band.Generation != asked.Generation ? "band generation differs from the request" : null;
    }

    /// <summary>Applies the effects of a validated reply and builds what the caller receives. Gate held.</summary>
    private Outcome BuildOutcome(Pending pending, ResponseEnvelope response)
    {
        switch (response.Payload)
        {
            case OpenDocumentReply opened when response.Status == Status.Ok:
                _documents[opened.DocumentId] = opened.PageCount;
                break;
            case CloseDocumentReply:
                _documents.Remove(pending.DocumentId);
                break;
            case RenderTileReply tile:
                var lease = pending.Slot!;
                pending.Slot = null; // now the tile's
                return new Outcome(response.Status, null, new TileLease(lease, (int)tile.Width, (int)tile.Height, (int)tile.Stride, tile.Generation, response.Status == Status.PartiallyRendered), null);
            case RenderBandReply band:
                var bandLease = pending.Slot!;
                pending.Slot = null; // now the band's
                return new Outcome(response.Status, null, new TileLease(bandLease, (int)band.Width, (int)band.Height, (int)band.Stride, band.Generation, response.Status == Status.PartiallyRendered), null);
            case Pong pong:
                ObserveProgress(pong.Progress);
                break;
            default:
                break;
        }

        return new Outcome(response.Status, response.Payload, null, null);
    }

    // ---- faults --------------------------------------------------------------------------------

    private void FaultWith(EngineFaultKind kind, string detail)
    {
        EngineFault fault;
        List<Pending> doomed;
        lock (_gate)
        {
            if (_fault is not null)
            {
                return;
            }

            fault = _fault = new EngineFault(kind, detail);
            doomed = [.. _pending.Values];
            _pending.Clear();
            _liveness?.Dispose();
            _liveness = null;
        }

        foreach (var pending in doomed)
        {
            pending.Timer?.Dispose();
            pending.CancellationRegistration.Dispose();
            pending.Slot?.Dispose();
            pending.Completion.TrySetResult(new Outcome(Status.EngineCrashed, null, null, detail));
        }

        _inFlight.Release(1 << 20); // wake senders waiting for room; they will see the fault
        _pipe.Dispose();
        if (kind != EngineFaultKind.Disposed)
        {
            Notify(() => Faulted?.Invoke(fault));
        }
    }

    private static void Notify(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Subscribers must not throw; a faulty one must not stop the client from finishing its clean-up.
        }
    }

    // ---- bookkeeping ---------------------------------------------------------------------------

    private readonly record struct Outcome(Status Status, IReplyMessage? Reply, TileLease? Tile, string? Detail);

    private sealed class Pending
    {
        public ulong Id;
        public MessageType Type;
        public uint DocumentId;
        public bool EngineQueue;
        public bool InFlightTaken;
        public bool Abandoned; // guarded by the client's gate
        public long OverdueSince; // Stopwatch timestamp of the moment the request became overdue, 0 while it is not; guarded by the client's gate
        public ulong PingNonce;
        public GetPageSizes? Sizes;
        public IRequestMessage? Request; // the reading services: what was asked, for ValidateReply
        public RenderTile? Render;
        public RenderBand? Band;
        public SlotLease? Slot;
        public Action? OnCancel;
        public Timer? Timer;
        public CancellationTokenRegistration CancellationRegistration;

        public TaskCompletionSource<Outcome> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
