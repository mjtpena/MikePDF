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

        // Schema 7: the annotation read side, text quads, forms, the engine's save and the tail normaliser.
        new("req_get_text_quads", 50, 7, 1000, new GetTextQuads { Page = 41, Start = 199_999, Count = 200_000, FirstQuad = 4095 }),
        new("req_get_text_quads_first_word", 51, 7, 1000, new GetTextQuads { Page = 0, Start = 0, Count = 1, FirstQuad = 0 }),
        new("req_list_annotations", 52, 7, 1000, new ListAnnotations { Page = 9_999_999, First = 99_999, Count = 64 }),
        new("req_list_annotations_first_window", 53, 7, 1000, new ListAnnotations { Page = 0, First = 0, Count = 1 }),
        new("req_get_annotation", 54, 7, 1000, new GetAnnotation { Page = 3, Index = 99_999 }),
        new("req_get_ink_stroke", 55, 7, 1000, new GetInkStroke { Page = 3, Index = 12, Stroke = 99_999, First = 9_999_999, Count = 1024 }),
        new("req_set_annotation_hidden", 56, 7, 1000, new SetAnnotationHidden { Page = 3, Index = 12, Hidden = true }),
        new("req_set_annotation_shown", 57, 7, 1000, new SetAnnotationHidden { Page = 3, Index = 12, Hidden = false }),
        new("req_count_annotations", 58, 7, 5000, new CountAnnotations { FirstPage = 9_999_999, Count = 5000 }),
        new("req_get_form_fields", 59, 7, 1000, new GetFormFields { First = 99_999, Count = 64 }),
        new("req_get_form_field_detail", 60, 7, 1000, new GetFormFieldDetail { FieldId = 99_999, FirstOption = 9999 }),
        new("req_set_form_field_value_choice", 63, 7, 1000, new SetFormFieldValue { FieldId = 6, Kind = 2, Text = "", Checked = false, Selected = [0, 9999] }),
        new("req_set_form_field_value_text", 61, 7, 1000, new SetFormFieldValue { FieldId = 4, Kind = 0, Text = "Ünïcode ✓ text", Checked = false, Selected = [] }),
        new("req_set_form_field_value_toggle", 62, 7, 1000, new SetFormFieldValue { FieldId = 5, Kind = 1, Text = "", Checked = true, Selected = [] }),
        new("req_form_action_reset", 64, 7, 1000, new FormAction { FieldId = 99_999, Action = 0 }),
        new("req_form_action_activate", 65, 7, 1000, new FormAction { FieldId = 2, Action = 2 }),
        new("req_save_document", 66, 7, 90000, new SaveDocument { Mode = 0, Flags = 0, OutputHandle = 0x2A4 }),
        new("req_save_document_full", 67, 7, 90000, new SaveDocument { Mode = 2, Flags = 3, OutputHandle = 0xFFFFFFFFFFFFFFFFUL }),
        new("req_normalize_tail", 68, 0, 90000, new NormalizeTail { OriginalHandle = 0x1F4, PdfiumHandle = 0x1F8, OutputHandle = 0x1FC, Flags = 3 }),
        new("req_normalize_tail_smallest", 69, 0, 1, new NormalizeTail { OriginalHandle = 1, PdfiumHandle = 1, OutputHandle = 1, Flags = 0 }),
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

        // Schema 7.
        new("rsp_get_text_quads", 50, Status.Ok, MessageType.GetTextQuads, new GetTextQuadsReply
        {
            TotalQuads = 2, Truncated = false,
            Xs = [72f, 144f, 72f, 144f, 72f, 130.5f, 72f, 130.5f],
            Ys = [712f, 712f, 700f, 700f, 698f, 698f, 686f, 686f],
        }),
        new("rsp_get_text_quads_none", 51, Status.Ok, MessageType.GetTextQuads, new GetTextQuadsReply { TotalQuads = 4096, Truncated = true, Xs = [], Ys = [] }),
        new("rsp_list_annotations", 52, Status.Ok, MessageType.ListAnnotations, new ListAnnotationsReply
        {
            Total = 100_000,
            Subtypes = [9, 1, 20, 28],
            Flags = [4, 28, 0, 0xFFFFFFFFu],
            Lefts = [72f, 20f, -1_000_000f, 0f],
            Bottoms = [700f, 20f, 0f, 0f],
            Rights = [144f, 44f, 1_000_000f, 0f],
            Tops = [712f, 44f, 10f, 0f],
            Colours = [0xFFFFEB00u, 0xFF000000u, 0u, 1u],
            Nms = ["MikePDF:0123456789abcdef0123456789abcdef", "", new string('n', 64), "é"],
            Authors = ["A. Reviewer", "", new string('a', 128), "ü"],
            Modified = ["D:20241103123456+10'00'", "", new string('m', 32), ""],
            Snippets = ["The marked text ✓", "", new string('s', 192), ""],
            Parents = [-1, 0, 99_999, -1],
        }),
        new("rsp_list_annotations_empty", 53, Status.Ok, MessageType.ListAnnotations, new ListAnnotationsReply
        {
            Total = 0, Subtypes = [], Flags = [], Lefts = [], Bottoms = [], Rights = [], Tops = [], Colours = [], Nms = [], Authors = [], Modified = [], Snippets = [], Parents = [],
        }),
        new("rsp_get_annotation_ink", 55, Status.Ok, MessageType.GetAnnotation, new GetAnnotationReply
        {
            Subtype = 15, Flags = 0xFFFFFFFFu, Left = -1_000_000f, Bottom = -1_000_000f, Right = 1_000_000f, Top = 1_000_000f, Colour = 0xFF0000FFu, InteriorColour = 0xFF00FF00u, Opacity = 1f,
            BorderWidth = 1000f, BorderStyle = 4, Contents = new string('c', 16384), ContentsTruncated = true, Author = new string('a', 256), Subject = new string('s', 256), Nm = new string('n', 64),
            Created = new string('d', 32), Modified = new string('m', 32), ParentIndex = 99_999, ReplyType = 2, StateModel = "Review", State = "Accepted", PopupOpen = true, Name = new string('i', 64),
            QuadPoints = new float[512], Vertices = new float[2048], VerticesTruncated = true, LineEndings = [10, 0], StrokeCount = 100_000, PointCount = 10_000_000,
            DefaultAppearance = new string('f', 256), Alignment = 2, HasAppearance = false,
        }),
        new("rsp_get_annotation", 54, Status.Ok, MessageType.GetAnnotation, new GetAnnotationReply
        {
            Subtype = 9, Flags = 4, Left = 72f, Bottom = 700f, Right = 144f, Top = 712f, Colour = 0xFFFFEB00u, InteriorColour = 0, Opacity = 0.5f, BorderWidth = 1f, BorderStyle = 0,
            Contents = "A comment ✓", ContentsTruncated = false, Author = "A. Reviewer", Subject = "Highlight", Nm = "MikePDF:0123456789abcdef0123456789abcdef",
            Created = "D:20241103123456+10'00'", Modified = "D:20241103123456+10'00'", ParentIndex = -1, ReplyType = 0, StateModel = "", State = "", PopupOpen = false, Name = "",
            QuadPoints = [72f, 712f, 144f, 712f, 72f, 700f, 144f, 700f], Vertices = [], VerticesTruncated = false, LineEndings = [], StrokeCount = 0, PointCount = 0,
            DefaultAppearance = "", Alignment = 0, HasAppearance = true,
        }),
        new("rsp_get_ink_stroke", 56, Status.Ok, MessageType.GetInkStroke, new GetInkStrokeReply
        {
            PointCount = 3, Xs = [10f, 11.5f, 13f], Ys = [20f, 22f, 21f], HasWidths = false, Widths = [0f, 0f, 0f],
        }),
        new("rsp_get_ink_stroke_widths", 57, Status.Ok, MessageType.GetInkStroke, new GetInkStrokeReply
        {
            PointCount = 10_000_000, Xs = [1f], Ys = [2f], HasWidths = true, Widths = [1000f],
        }),
        new("rsp_set_annotation_hidden", 58, Status.Ok, MessageType.SetAnnotationHidden, new SetAnnotationHiddenReply()),
        new("rsp_count_annotations", 59, Status.Ok, MessageType.CountAnnotations, new CountAnnotationsReply { Counts = [0, 3, 65535, 12] }),
        new("rsp_get_form_fields", 60, Status.Ok, MessageType.GetFormFields, new GetFormFieldsReply
        {
            Total = 100_000,
            FieldIds = [0, 99_999, 7],
            Pages = [0, 9_999_999, 3],
            Lefts = [72f, 100f, 50f],
            Bottoms = [700f, 100f, 40f],
            Rights = [300f, 120f, 90f],
            Tops = [720f, 120f, 60f],
            Types = [6, 2, 4],
            Flags = [0, 1, 0x00262000u],
            States = [0, 1, 15],
            MaxLens = [0, 10, 65535],
            Names = ["name", "agree", new string('f', 128)],
            AlternateNames = ["Full name", "", "É"],
            Values = ["Alice ✓", "", new string('v', 128)],
        }),
        new("rsp_get_form_fields_none", 61, Status.Ok, MessageType.GetFormFields, new GetFormFieldsReply
        {
            Total = 0, FieldIds = [], Pages = [], Lefts = [], Bottoms = [], Rights = [], Tops = [], Types = [], Flags = [], States = [], MaxLens = [], Names = [], AlternateNames = [], Values = [],
        }),
        new("rsp_get_form_field_detail", 62, Status.Ok, MessageType.GetFormFieldDetail, new GetFormFieldDetailReply
        {
            Type = 4, Flags = 0x00060000u, Name = "country", AlternateName = "Country of residence", Value = "France", ValueTruncated = false, DefaultValue = "France", MaxLen = 0,
            Quadding = 1, FontSize = 12f, ExportValue = "", Checked = false, WidgetCount = 1, OptionsTotal = 10_000, OptionLabels = ["France", "Germany"], OptionValues = ["France", "Germany"],
            SelectedIndices = [0], HasFormatScript = false, HasValidateScript = true, HasCalculateScript = false,
        }),
        new("rsp_get_form_field_detail_maximal", 63, Status.Ok, MessageType.GetFormFieldDetail, new GetFormFieldDetailReply
        {
            Type = 7, Flags = 0xFFFFFFFFu, Name = new string('n', 512), AlternateName = new string('a', 256), Value = new string('v', 8192), ValueTruncated = true, DefaultValue = new string('d', 8192),
            MaxLen = 65535, Quadding = 2, FontSize = 1000f, ExportValue = new string('e', 64), Checked = true, WidgetCount = 100_000, OptionsTotal = 10_000,
            OptionLabels = Enumerable.Repeat(new string('l', 128), 64).ToArray(), OptionValues = Enumerable.Repeat(new string('o', 128), 64).ToArray(),
            SelectedIndices = Enumerable.Repeat(9999u, 64).ToArray(), HasFormatScript = true, HasValidateScript = true, HasCalculateScript = true,
        }),
        new("rsp_set_form_field_value", 64, Status.Ok, MessageType.SetFormFieldValue, new SetFormFieldValueReply { Accepted = true, Value = "Alice ✓", AffectedPages = [3, 4, 9_999_999], Truncated = false }),
        new("rsp_set_form_field_value_refused", 65, Status.Ok, MessageType.SetFormFieldValue, new SetFormFieldValueReply { Accepted = false, Value = new string('v', 8192), AffectedPages = Enumerable.Repeat(1u, 256).ToArray(), Truncated = true }),
        new("rsp_form_action", 66, Status.Ok, MessageType.FormAction, new FormActionReply { AffectedPages = [0, 1, 2], Performed = 1, Truncated = false }),
        new("rsp_form_action_refused", 67, Status.Ok, MessageType.FormAction, new FormActionReply { AffectedPages = [], Performed = 3, Truncated = true }),
        new("rsp_save_document", 68, Status.Ok, MessageType.SaveDocument, new SaveDocumentReply { BytesWritten = 1_099_511_627_776UL, PageCount = 10_000_000 }),
        new("rsp_save_document_small", 69, Status.Ok, MessageType.SaveDocument, new SaveDocumentReply { BytesWritten = 1_234, PageCount = 1 }),
        new("rsp_normalize_tail_refused", 70, Status.Ok, MessageType.NormalizeTail, new NormalizeTailReply
        {
            Ok = false, BytesWritten = 0, Truncated = true, IssueCodes = [30, 65535], IssueMessages = ["error: the PDFium file does not begin with the original", "café"],
        }),
        new("rsp_normalize_tail", 71, Status.Ok, MessageType.NormalizeTail, new NormalizeTailReply { Ok = true, BytesWritten = 2_147_483_647, Truncated = false, IssueCodes = [], IssueMessages = [] }),
        new("rsp_save_document_permission_denied", 72, Status.PermissionDenied, MessageType.SaveDocument, null),
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
