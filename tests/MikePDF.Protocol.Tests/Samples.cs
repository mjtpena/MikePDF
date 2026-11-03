namespace MikePDF.Protocol.Tests;

/// <summary>A request frame definition: the envelope fields and a valid payload.</summary>
public sealed record RequestSample(string Name, ulong RequestId, uint DocumentId, uint DeadlineMs, IRequestMessage Payload);

/// <summary>A response frame definition. Payload is null for error statuses.</summary>
public sealed record ResponseSample(string Name, ulong RequestId, Status Status, MessageType Type, IReplyMessage? Payload);

/// <summary>One valid example of every request and reply. These are also the golden vectors.</summary>
public static class Samples
{
    public static IReadOnlyList<RequestSample> Requests { get; } =
    [
        new("req_hello", 1, 0, 5000, new Hello { SchemaVersion = ProtocolInfo.SchemaVersion, ProcessKind = ProcessKind.Broker, Pid = 4242 }),
        new("req_ping", 2, 0, 0, new Ping { Nonce = 0x1122334455667788UL }),
        new("req_shutdown", 3, 0, 1000, new Shutdown()),
        new("req_attach_slot_ring", 12, 0, 5000, new AttachSlotRing { Handle = 0x2F4, SlotCount = 8, SlotSize = 4_194_304 }),
        new("req_open_document", 4, 0, 30000, new OpenDocument { Handle = 0x1F4, Password = "pässwörd✓" }),
        new("req_open_document_no_password", 5, 0, 30000, new OpenDocument { Handle = 0xFFFFFFFFFFFFFFFFUL, Password = null }),
        new("req_close_document", 6, 7, 1000, new CloseDocument()),
        new("req_get_document_info", 7, 7, 1000, new GetDocumentInfo()),
        new("req_get_page_sizes", 8, 7, 1000, new GetPageSizes { First = 100, Count = 5000 }),
        new("req_render_tile", 9, 7, 250, new RenderTile
        {
            Page = 3, Scale = 1.5f, Rotation = 1, TileX = -256, TileY = 512, TileWidth = 256, TileHeight = 256,
            Flags = 3, SlotIndex = 12, Generation = 9_000_000_000UL,
        }),
        new("req_cancel_before", 10, 7, 0, new CancelBefore { Generation = 77 }),
        new("req_validate_structure", 11, 0, 60000, new ValidateStructure { Handle = 1234 }),
        new("req_get_outline", 13, 7, 1000, new GetOutline { Path = [2, 0, 5], First = 64, Count = 64 }),
        new("req_get_outline_top_level", 14, 7, 1000, new GetOutline { Path = [], First = 0, Count = 1 }),
        new("req_get_page_links", 15, 7, 1000, new GetPageLinks { Page = 41, First = 512, Count = 512 }),
        new("req_get_link_uri", 16, 7, 1000, new GetLinkUri { Page = 41, Index = 99999 }),
        new("req_get_page_boxes", 17, 7, 1000, new GetPageBoxes { Page = 9999999 }),
        new("req_get_page_text", 18, 7, 1000, new GetPageText { Page = 3, First = 2048, Count = 2048 }),
        new("req_search_page", 19, 7, 1000, new SearchPage { Page = 3, Query = "façade ✓", Flags = 3 }),
        new("req_get_document_details", 20, 7, 1500, new GetDocumentDetails()),

        // Schema 5.
        new("req_get_viewer_preferences", 21, 7, 1000, new GetViewerPreferences()),
        new("req_render_band", 22, 7, 30000, new RenderBand
        {
            Page = 3, Dpi = 600, Rotation = 1, PageWidthPx = 65536, PageHeightPx = 65536, BandTop = 49152, BandHeight = 16384, Flags = 7, SlotIndex = 12, Generation = 9_000_000_000UL,
        }),
        new("req_render_band_typical", 23, 7, 30000, new RenderBand
        {
            Page = 0, Dpi = 150, Rotation = 0, PageWidthPx = 1275, PageHeightPx = 1650, BandTop = 0, BandHeight = 256, Flags = 5, SlotIndex = 1, Generation = 1,
        }),
        new("req_hello_shell_handler", 24, 0, 5000, new Hello { SchemaVersion = ProtocolInfo.SchemaVersion, ProcessKind = ProcessKind.ShellHandler, Pid = 4242 }),
        new("req_shell_open", 25, 0, 1700, new ShellOpen { Section = 0x3A8, Bytes = 33_554_432 }),
        new("req_shell_render_page", 26, 3, 1500, new ShellRenderPage
        {
            Page = 9999999, MaxWidth = 1024, MaxHeight = 1024, Flags = 1, Pixels = 0x3B0, PixelBytes = 4_194_304,
        }),
        new("req_shell_render_page_smallest", 28, 3, 1, new ShellRenderPage { Page = 0, MaxWidth = 16, MaxHeight = 16, Flags = 0, Pixels = 1, PixelBytes = 4 }),
        new("req_shell_close", 27, 3, 500, new ShellClose()),

        // Schema 6: the writer host's save messages (save-pipeline 3.3).
        new("req_append_annotations", 40, 0, 90000, new AppendAnnotations { BaseHandle = 0x1F4, EditsHandle = 0x1F8, EditsLength = 268_435_456, OutputHandle = 0x1FC, PageCount = 10_000_000 }),
        new("req_append_annotations_smallest", 41, 0, 1, new AppendAnnotations { BaseHandle = 1, EditsHandle = 1, EditsLength = 1, OutputHandle = 1, PageCount = 0 }),
        new("req_rewrite_document", 42, 0, 90000, new RewriteDocument { BaseHandle = 0x1F4, OutputHandle = 0x1FC, Flags = 3 }),
        new("req_rewrite_document_plain", 43, 0, 90000, new RewriteDocument { BaseHandle = 0x1F4, OutputHandle = 0x1FC, Flags = 0 }),
        new("req_verify_annotations", 44, 0, 90000, new VerifyAnnotations
        {
            BaseHandle = 0x1F4, OutputHandle = 0x1FC, PageCount = 342, Pages = [0, 9999999], Added = [3, 0], Removed = [1, 100_000_000],
        }),
        new("req_verify_annotations_no_pages", 45, 0, 90000, new VerifyAnnotations { BaseHandle = 1, OutputHandle = 2, PageCount = 1, Pages = [], Added = [], Removed = [] }),
    ];

    public static IReadOnlyList<ResponseSample> Responses { get; } =
    [
        new("rsp_hello", 1, Status.Ok, MessageType.Hello, new HelloReply { SchemaVersion = ProtocolInfo.SchemaVersion, PdfiumVersion = "PDFium 8125.0.0.0" }),
        new("rsp_pong", 2, Status.Ok, MessageType.Ping, new Pong { Nonce = 0x1122334455667788UL, Progress = 0xFEDCBA9876543210UL }),
        new("rsp_pong_idle", 28, Status.Ok, MessageType.Ping, new Pong { Nonce = 1, Progress = 0 }),
        new("rsp_shutdown", 3, Status.Ok, MessageType.Shutdown, new ShutdownReply()),
        new("rsp_attach_slot_ring", 12, Status.Ok, MessageType.AttachSlotRing, new AttachSlotRingReply()),
        new("rsp_open_document", 4, Status.Ok, MessageType.OpenDocument, new OpenDocumentReply { DocumentId = 7, PageCount = 342 }),
        new("rsp_close_document", 6, Status.Ok, MessageType.CloseDocument, new CloseDocumentReply()),
        new("rsp_get_document_info", 7, Status.Ok, MessageType.GetDocumentInfo, new GetDocumentInfoReply
        {
            PageCount = 342, PdfVersion = 17, Title = "MikePDF — user guide", Author = "A. Writer", Subject = "", Keywords = "pdf, reader",
            Creator = "Word", Producer = "MikePDF 1.0", Permissions = 0xFFFFF0C4, IsTagged = true, HasXfa = false, HasJavaScript = true, SignatureCount = 2,
        }),
        new("rsp_get_page_sizes", 8, Status.Ok, MessageType.GetPageSizes, new GetPageSizesReply
        {
            Widths = [612f, 595.276f, 841.89f], Heights = [792f, 841.89f, 595.276f], Rotations = [0, 1, 3],
        }),
        new("rsp_render_tile", 9, Status.Ok, MessageType.RenderTile, new RenderTileReply { SlotIndex = 12, Width = 256, Height = 256, Stride = 1024, Generation = 9_000_000_000UL }),
        new("rsp_render_tile_partial", 9, Status.PartiallyRendered, MessageType.RenderTile, new RenderTileReply { SlotIndex = 13, Width = 100, Height = 50, Stride = 400, Generation = 1 }),
        new("rsp_cancel_before", 10, Status.Ok, MessageType.CancelBefore, new CancelBeforeReply()),
        new("rsp_validate_structure", 11, Status.Ok, MessageType.ValidateStructure, new ValidateStructureReply
        {
            Ok = false, Truncated = false, IssueCodes = [3, 65535], IssueMessages = ["xref entry 12 points outside the file", "café"],
        }),
        new("rsp_get_outline", 13, Status.Ok, MessageType.GetOutline, new GetOutlineReply
        {
            Total = 3,
            Titles = ["Introduction", "Chapter 2 — Fonts ✓", ""],
            Kinds = [3, 1, 0],
            Pages = [0, 41, 0],
            Xs = [72f, 0f, 0f],
            Ys = [700.5f, 0f, 0f],
            HasChildren = [1, 0, 0],
        }),
        new("rsp_get_page_links", 15, Status.Ok, MessageType.GetPageLinks, new GetPageLinksReply
        {
            Total = 2,
            Lefts = [72f, -10.5f],
            Bottoms = [700f, 20f],
            Rights = [144f, 100f],
            Tops = [712f, 32f],
            Kinds = [2, 4],
            Pages = [7, 0],
            Xs = [0f, 0f],
            Ys = [640.25f, 0f],
        }),
        new("rsp_get_link_uri", 16, Status.Ok, MessageType.GetLinkUri, new GetLinkUriReply { Uri = "https://example.org/a%20b?q=ü" }),
        new("rsp_get_page_boxes", 17, Status.Ok, MessageType.GetPageBoxes, new GetPageBoxesReply
        {
            MediaLeft = 0f, MediaBottom = 0f, MediaRight = 612f, MediaTop = 792f,
            HasCropBox = true, CropLeft = 100f, CropBottom = 150f, CropRight = 500f, CropTop = 650f, Rotation = 1,
        }),
        new("rsp_get_page_text", 18, Status.Ok, MessageType.GetPageText, new GetPageTextReply
        {
            Total = 5000,
            Truncated = false,
            Units = [72, 105, 0xFFFD, 10],
            Lefts = [72f, 79f, 85f, 90f],
            Bottoms = [700f, 700f, 700f, 700f],
            Rights = [79f, 85f, 90f, 90f],
            Tops = [712f, 712f, 712f, 712f],
            Flags = [0, 0, 2, 1],
        }),
        new("rsp_search_page", 19, Status.Ok, MessageType.SearchPage, new SearchPageReply { Truncated = true, Starts = [0, 199000], Lengths = [5, 200000 - 199000] }),
        new("rsp_get_document_details", 20, Status.Ok, MessageType.GetDocumentDetails, new GetDocumentDetailsReply
        {
            CreationDate = "D:20240115103000+10'00'", ModificationDate = "D:20240201", SecurityRevision = 4, UserPermissions = 0xFFFFF2C0, FormType = 1,
            FontNames = ["ABCDEF+Arial-BoldMT", "Helvetica", "TimesNewRomanPSMT"], FontFlags = [1, 0, 1], FontsComplete = false, FontPagesScanned = 100,
        }),
        new("rsp_get_document_details_empty", 21, Status.Ok, MessageType.GetDocumentDetails, new GetDocumentDetailsReply
        {
            CreationDate = "", ModificationDate = "", SecurityRevision = 0, UserPermissions = 0xFFFFFFFF, FormType = 0,
            FontNames = [], FontFlags = [], FontsComplete = true, FontPagesScanned = 0,
        }),

        // Schema 5.
        new("rsp_get_viewer_preferences", 21, Status.Ok, MessageType.GetViewerPreferences, new GetViewerPreferencesReply
        {
            Direction = 1, PrintScaling = 1, NumCopies = 3, Duplex = 3, RangeFirsts = [0, 9999999], RangeLasts = [2, 9999999],
        }),
        new("rsp_get_viewer_preferences_absent", 29, Status.Ok, MessageType.GetViewerPreferences, new GetViewerPreferencesReply
        {
            Direction = 2, PrintScaling = 0, NumCopies = 0, Duplex = 0, RangeFirsts = [], RangeLasts = [],
        }),
        new("rsp_render_band", 22, Status.Ok, MessageType.RenderBand, new RenderBandReply { SlotIndex = 12, Width = 65536, Height = 16384, Stride = 262144, Generation = 9_000_000_000UL }),
        new("rsp_render_band_partial", 23, Status.PartiallyRendered, MessageType.RenderBand, new RenderBandReply { SlotIndex = 1, Width = 1275, Height = 256, Stride = 5100, Generation = 1 }),
        new("rsp_shell_open", 25, Status.Ok, MessageType.ShellOpen, new ShellOpenReply { DocumentId = 3, PageCount = 342, FirstWidth = 612f, FirstHeight = 792f }),
        new("rsp_shell_render_page", 26, Status.Ok, MessageType.ShellRenderPage, new ShellRenderPageReply { Width = 1024, Height = 768, Stride = 4096 }),
        new("rsp_shell_close", 27, Status.Ok, MessageType.ShellClose, new ShellCloseReply()),

        // Schema 6. (A refusal comes first for the two with issue lists: the generic limit tests mutate the first sample of a type, which needs elements.)
        new("rsp_append_annotations_refused", 41, Status.Ok, MessageType.AppendAnnotations, new AppendAnnotationsReply
        {
            Ok = false, AnnotationsAdded = 0, AnnotationsRemoved = 0, ObjectsWritten = 0, BytesWritten = 0, Truncated = true, IssueCodes = [30, 65535], IssueMessages = ["error: an annotation's rectangle is not the one the edit expected", "café"],
        }),
        new("rsp_append_annotations", 40, Status.Ok, MessageType.AppendAnnotations, new AppendAnnotationsReply
        {
            Ok = true, AnnotationsAdded = 100_000_000, AnnotationsRemoved = 3, ObjectsWritten = 100_000_000, BytesWritten = 2_147_483_647, Truncated = false, IssueCodes = [], IssueMessages = [],
        }),
        new("rsp_append_annotations_timeout", 46, Status.Timeout, MessageType.AppendAnnotations, null),
        new("rsp_rewrite_document_refused", 43, Status.Ok, MessageType.RewriteDocument, new RewriteDocumentReply
        {
            Ok = false, PagesChanged = 0, AnnotationsFlattened = 0, AnnotationsRemoved = 0, ObjectsWritten = 0, BytesWritten = 0, Truncated = false, IssueCodes = [1], IssueMessages = ["error: the writer cannot rewrite an encrypted document"],
        }),
        new("rsp_rewrite_document", 42, Status.Ok, MessageType.RewriteDocument, new RewriteDocumentReply
        {
            Ok = true, PagesChanged = 10_000_000, AnnotationsFlattened = 12, AnnotationsRemoved = 14, ObjectsWritten = 5000, BytesWritten = 1_048_576, Truncated = false, IssueCodes = [], IssueMessages = [],
        }),
        new("rsp_verify_annotations", 44, Status.Ok, MessageType.VerifyAnnotations, new VerifyAnnotationsReply { Ok = true, Mismatches = [7, 9999999] }),
        new("rsp_verify_annotations_clean", 45, Status.Ok, MessageType.VerifyAnnotations, new VerifyAnnotationsReply { Ok = true, Mismatches = [] }),
        new("rsp_error_password_required", 4, Status.PasswordRequired, MessageType.OpenDocument, null),
        new("rsp_error_timeout", 9, Status.Timeout, MessageType.RenderTile, null),
    ];

    /// <summary>A valid instance of every payload type, keyed by type name, for the schema-driven limit tests.</summary>
    public static IReadOnlyDictionary<string, object> ByTypeName { get; } =
        Requests.Select(r => (object)r.Payload)
            .Concat(Responses.Where(r => r.Payload is not null).Select(r => (object)r.Payload!))
            .GroupBy(o => o.GetType().Name)
            .ToDictionary(g => g.Key, g => g.First());
}
