namespace Quire.Protocol.Tests;

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
    ];

    public static IReadOnlyList<ResponseSample> Responses { get; } =
    [
        new("rsp_hello", 1, Status.Ok, MessageType.Hello, new HelloReply { SchemaVersion = ProtocolInfo.SchemaVersion, PdfiumVersion = "PDFium 8125.0.0.0" }),
        new("rsp_pong", 2, Status.Ok, MessageType.Ping, new Pong { Nonce = 0x1122334455667788UL }),
        new("rsp_shutdown", 3, Status.Ok, MessageType.Shutdown, new ShutdownReply()),
        new("rsp_open_document", 4, Status.Ok, MessageType.OpenDocument, new OpenDocumentReply { DocumentId = 7, PageCount = 342 }),
        new("rsp_close_document", 6, Status.Ok, MessageType.CloseDocument, new CloseDocumentReply()),
        new("rsp_get_document_info", 7, Status.Ok, MessageType.GetDocumentInfo, new GetDocumentInfoReply
        {
            PageCount = 342, PdfVersion = 17, Title = "Quire — user guide", Author = "A. Writer", Subject = "", Keywords = "pdf, reader",
            Creator = "Word", Producer = "Quire PDF 1.0", Permissions = 0xFFFFF0C4, IsTagged = true, HasXfa = false, HasJavaScript = true, SignatureCount = 2,
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
