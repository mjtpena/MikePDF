using MessagePack;

namespace MikePDF.Protocol.Tests;

/// <summary>A frame that must be rejected, and the status both decoders must give.</summary>
public sealed record InvalidVector(string Name, bool IsRequest, byte[] Frame, Status Expected);

/// <summary>
/// The golden frames: every valid sample plus a set of malformed ones, each malformed in exactly one way. The C# tests
/// write them to tests/MikePDF.Protocol.Tests/golden (set MIKEPDF_UPDATE_GOLDEN=1), and the C++ tests read the same files.
/// </summary>
public static class GoldenVectors
{
    private const Status Protocol = Status.ProtocolError;
    private const Status Invalid = Status.InvalidArgument;

    private static byte[] Valid(string name) => Samples.Requests.Select(r => (r.Name, Frame: ProtocolCodec.EncodeRequestFrame(r.RequestId, r.DocumentId, r.DeadlineMs, r.Payload).Value))
        .Concat(Samples.Responses.Select(r => (r.Name, Frame: ProtocolCodec.EncodeResponseFrame(r.RequestId, r.Status, r.Type, r.Payload).Value)))
        .First(x => x.Name == name).Frame;

    // Payload writers. A writer cannot be captured, so each is a delegate that takes it by reference.
    private static WriteBody Items(params object[] items) => (ref MessagePackWriter w) =>
    {
        w.WriteArrayHeader(items.Length);
        foreach (var item in items) WriteItem(ref w, item);
    };

    private static void WriteItem(ref MessagePackWriter w, object item)
    {
        switch (item)
        {
            case ulong u: w.Write(u); break;
            case uint u: w.Write(u); break;
            case ushort u: w.Write(u); break;
            case int i: w.Write(i); break;
            case float f: w.Write(f); break;
            case double d: w.Write(d); break;
            case bool b: w.Write(b); break;
            case string s: w.Write(s); break;
            case byte[] raw: w.WriteRaw(raw); break;
            case null: w.WriteNil(); break;
            default: throw new ArgumentException(item.GetType().Name);
        }
    }

    private static readonly byte[] RawNan = RawFloat32(0x7FC00000);
    private static readonly byte[] RawInf = RawFloat32(0x7F800000);

    private static byte[] RawFloat32(uint bits) => [0xCA, (byte)(bits >> 24), (byte)(bits >> 16), (byte)(bits >> 8), (byte)bits];

    private static byte[] RawString(params byte[] utf8) => RawFrame.Bytes((ref MessagePackWriter w) =>
    {
        w.WriteStringHeader(utf8.Length);
        w.WriteRaw(utf8);
    });

    private static RenderTile Tile() => (RenderTile)Samples.Requests.First(r => r.Name == "req_render_tile").Payload;

    // The RenderTile payload as raw items, with one item replaced.
    private static byte[] TileWith(int index, object replacement)
    {
        var t = Tile();
        object[] items =
        [
            t.Page, t.Scale, t.Rotation, t.TileX, t.TileY, t.TileWidth, t.TileHeight, t.Flags, t.SlotIndex, t.Generation,
        ];
        items[index] = replacement;
        return RawFrame.Request(Items(items), MessageType.RenderTile, 9, 7, 250);
    }

    private static byte[] Deep(int depth) => RawFrame.Request(
        (ref MessagePackWriter w) =>
        {
            for (var i = 0; i < depth; i++) w.WriteArrayHeader(1);
            w.Write(1u);
        },
        MessageType.Ping);

    private static byte[] Concat(byte[] a, params byte[] b) => [.. a, .. b];

    public static IReadOnlyList<InvalidVector> InvalidFrames { get; } = Build();

    private static List<InvalidVector> Build()
    {
        var ping = Valid("req_ping");
        var v = new List<InvalidVector>();

        void Req(string name, byte[] frame, Status expected) => v.Add(new("bad_req_" + name, true, frame, expected));
        void Rsp(string name, byte[] frame, Status expected) => v.Add(new("bad_rsp_" + name, false, frame, expected));

        // framing
        Req("frame_too_large", [0x01, 0x00, 0x01, 0x00, 0x91, 0x00], Protocol); // declares 65537 bytes
        Req("frame_empty", [0, 0, 0, 0], Protocol);
        Req("frame_header_short", [0x05, 0x00], Protocol);
        Req("frame_truncated", ping[..^3], Protocol);
        Req("frame_trailing_bytes", Concat(ping, 0x00, 0x00), Protocol);
        Req("body_trailing_bytes", RawFrame.Prefix(Concat(ping[4..], 0xC0)), Protocol);

        // envelope
        Req("header_four_elements", RawFrame.Prefix(RawFrame.Bytes((ref MessagePackWriter w) =>
        {
            w.WriteArrayHeader(4); w.Write(1UL); w.Write(0u); w.Write(0u); w.Write((ushort)MessageType.Ping);
        })), Protocol);
        Req("message_type_unknown", RawFrame.Request(Items(1UL), (MessageType)999), Protocol);
        Req("message_type_zero", RawFrame.Request(Items(1UL), (MessageType)0), Protocol);
        Req("payload_nil", RawFrame.Prefix(RawFrame.Bytes((ref MessagePackWriter w) =>
        {
            w.WriteArrayHeader(5); w.Write(1UL); w.Write(0u); w.Write(0u); w.Write((ushort)MessageType.Ping); w.WriteNil();
        })), Protocol);
        Req("document_required_but_zero", RawFrame.Request(Items(), MessageType.CloseDocument, 6, 0), Invalid);
        Req("document_given_but_not_allowed", RawFrame.Request(Items(1UL), MessageType.Ping, 2, 5), Invalid);
        Req("deadline_too_large", RawFrame.Request(Items(1UL), MessageType.Ping, 2, 0, ProtocolInfo.MaxDeadlineMs + 1), Invalid);

        // payload shape
        Req("payload_too_few_fields", RawFrame.Request(Items(), MessageType.Ping), Protocol);
        Req("payload_too_many_fields", RawFrame.Request(Items(1UL, 2UL), MessageType.Ping), Protocol);
        Req("payload_deeply_nested", Deep(1000), Protocol);

        // wrong wire types
        Req("string_for_uint", RawFrame.Request(Items("x"), MessageType.Ping), Protocol);
        Req("float_for_uint", RawFrame.Request(Items(1.0f), MessageType.Ping), Protocol);
        Req("negative_for_uint", RawFrame.Request(Items(-1), MessageType.Ping), Protocol);
        Req("float64_for_float32", TileWith(1, 1.5d), Protocol);
        Req("int_for_float32", TileWith(1, 2), Protocol);
        Req("uint_too_wide_for_u16", TileWith(2, 70000u), Protocol);
        Req("uint_too_wide_for_i32", TileWith(3, 2147483648u), Protocol);
        Req("bin_for_string", RawFrame.Request((ref MessagePackWriter w) =>
        {
            w.WriteArrayHeader(2); w.Write(1UL); w.Write(new byte[] { 1, 2 });
        }, MessageType.OpenDocument), Protocol);
        Req("nil_for_uint", RawFrame.Request(Items((object?)null!), MessageType.Ping), Protocol);

        // schema limits
        Req("scale_nan", TileWith(1, RawNan), Invalid);
        Req("scale_infinite", TileWith(1, RawInf), Invalid);
        Req("scale_too_small", TileWith(1, 0.0099f), Invalid);
        Req("scale_too_large", TileWith(1, 64.5f), Invalid);
        Req("rotation_four", TileWith(2, (ushort)4), Invalid);
        Req("tile_width_zero", TileWith(5, 0u), Invalid);
        Req("tile_width_1025", TileWith(5, 1025u), Invalid);
        Req("tile_x_out_of_range", TileWith(3, 16777217), Invalid);
        Req("flags_unknown_bit", TileWith(7, 8u), Invalid);
        Req("handle_zero", RawFrame.Request(Items(0UL, null!), MessageType.OpenDocument), Invalid);
        Req("attach_slot_ring_handle_zero", RawFrame.Request(Items(0UL, 8u, 4096u), MessageType.AttachSlotRing), Invalid);
        Req("attach_slot_ring_no_slots", RawFrame.Request(Items(1UL, 0u, 4096u), MessageType.AttachSlotRing), Invalid);
        Req("attach_slot_ring_1025_slots", RawFrame.Request(Items(1UL, 1025u, 4096u), MessageType.AttachSlotRing), Invalid);
        Req("attach_slot_ring_slot_below_a_page", RawFrame.Request(Items(1UL, 8u, 4095u), MessageType.AttachSlotRing), Invalid);
        Req("attach_slot_ring_slot_above_64_mib", RawFrame.Request(Items(1UL, 8u, 67108865u), MessageType.AttachSlotRing), Invalid);
        Req("attach_slot_ring_with_a_document", RawFrame.Request(Items(1UL, 8u, 4096u), MessageType.AttachSlotRing, 12, 7), Invalid);
        Req("password_1025_bytes", RawFrame.Request(Items(1UL, new string('a', 1025)), MessageType.OpenDocument), Invalid);
        Req("password_invalid_utf8", RawFrame.Request(Items(1UL, RawString(0xFF, 0xFE)), MessageType.OpenDocument), Protocol);
        Req("password_overlong_utf8", RawFrame.Request(Items(1UL, RawString(0xC0, 0x80)), MessageType.OpenDocument), Protocol);
        Req("password_with_nul", RawFrame.Request(Items(1UL, "a\0b"), MessageType.OpenDocument), Invalid);
        Req("hello_undefined_process_kind", RawFrame.Request(Items(1u, (ushort)4, 1u), MessageType.Hello), Invalid); // 3 is the shell handler since schema 5
        Req("hello_schema_version_zero", RawFrame.Request(Items(0u, (ushort)0, 1u), MessageType.Hello), Invalid);
        Req("page_sizes_count_zero", RawFrame.Request(Items(0u, 0u), MessageType.GetPageSizes, 8, 7), Invalid);
        Req("page_sizes_count_5001", RawFrame.Request(Items(0u, 5001u), MessageType.GetPageSizes, 8, 7), Invalid);

        // responses
        Rsp("status_unknown", RawFrame.Response(Items(1UL), MessageType.Ping, (Status)77), Protocol);
        Rsp("message_type_unknown", RawFrame.Response(Items(1UL), (MessageType)999), Protocol);
        Rsp("error_status_with_payload", RawFrame.Response(Items(1UL), MessageType.Ping, Status.Timeout), Protocol);
        Rsp("ok_status_with_nil", RawFrame.Response(null, MessageType.Ping), Protocol);
        Rsp("name_nil_for_required_string", RawFrame.Response(Items(1u, null!), MessageType.Hello), Protocol);
        Rsp("array_count_exceeds_message", RawFrame.Response(
            (ref MessagePackWriter w) =>
            {
                w.WriteArrayHeader(3);
                w.WriteRaw([0xDD, 0x00, 0x0F, 0x42, 0x40]); // an array of 1,000,000 elements, with none following
            },
            MessageType.GetPageSizes), Protocol);
        Rsp("page_widths_zero", RawFrame.Response(
            (ref MessagePackWriter w) =>
            {
                w.WriteArrayHeader(3);
                w.WriteArrayHeader(1); w.Write(0f);
                w.WriteArrayHeader(1); w.Write(10f);
                w.WriteArrayHeader(1); w.Write((ushort)0);
            },
            MessageType.GetPageSizes), Invalid);
        Rsp("page_widths_infinite", RawFrame.Response(
            (ref MessagePackWriter w) =>
            {
                w.WriteArrayHeader(3);
                w.WriteArrayHeader(1); w.WriteRaw(RawInf);
                w.WriteArrayHeader(1); w.Write(10f);
                w.WriteArrayHeader(1); w.Write((ushort)0);
            },
            MessageType.GetPageSizes), Invalid);
        Rsp("page_arrays_differ_in_length", RawFrame.Response(
            (ref MessagePackWriter w) =>
            {
                w.WriteArrayHeader(3);
                w.WriteArrayHeader(2); w.Write(10f); w.Write(10f);
                w.WriteArrayHeader(1); w.Write(10f);
                w.WriteArrayHeader(2); w.Write((ushort)0); w.Write((ushort)0);
            },
            MessageType.GetPageSizes), Invalid);
        Rsp("page_rotation_four", RawFrame.Response(
            (ref MessagePackWriter w) =>
            {
                w.WriteArrayHeader(3);
                w.WriteArrayHeader(1); w.Write(10f);
                w.WriteArrayHeader(1); w.Write(10f);
                w.WriteArrayHeader(1); w.Write((ushort)4);
            },
            MessageType.GetPageSizes), Invalid);
        Rsp("issues_257", RawFrame.Response(
            (ref MessagePackWriter w) =>
            {
                w.WriteArrayHeader(4); w.Write(false); w.Write(false);
                w.WriteArrayHeader(257);
                for (var i = 0; i < 257; i++) w.Write((uint)i);
                w.WriteArrayHeader(0);
            },
            MessageType.ValidateStructure), Invalid);
        Rsp("issue_message_193_bytes", RawFrame.Response(
            (ref MessagePackWriter w) =>
            {
                w.WriteArrayHeader(4); w.Write(false); w.Write(false);
                w.WriteArrayHeader(1); w.Write(1u);
                w.WriteArrayHeader(1); w.Write(new string('m', 193));
            },
            MessageType.ValidateStructure), Invalid);
        Rsp("issue_message_nil", RawFrame.Response(
            (ref MessagePackWriter w) =>
            {
                w.WriteArrayHeader(4); w.Write(false); w.Write(false);
                w.WriteArrayHeader(1); w.Write(1u);
                w.WriteArrayHeader(1); w.WriteNil();
            },
            MessageType.ValidateStructure), Protocol);
        Rsp("issue_counts_differ", RawFrame.Response(
            (ref MessagePackWriter w) =>
            {
                w.WriteArrayHeader(4); w.Write(false); w.Write(false);
                w.WriteArrayHeader(2); w.Write(1u); w.Write(2u);
                w.WriteArrayHeader(1); w.Write("only one");
            },
            MessageType.ValidateStructure), Invalid);
        Rsp("document_id_zero", RawFrame.Response(Items(0u, 5u), MessageType.OpenDocument), Invalid);
        Rsp("render_tile_zero_stride", RawFrame.Response(Items(0u, 4u, 4u, 0u, 1UL), MessageType.RenderTile), Invalid);

        // The reading services (schema 3). Requests are hand-made with the real writer, which does not validate.
        static byte[] Doc(object message, MessageType type) => RawFrame.Request(RawFrame.Serialized(message), type, 9, 7, 1000);
        Req("outline_count_zero", Doc(new GetOutline { Path = [], First = 0, Count = 0 }, MessageType.GetOutline), Invalid);
        Req("outline_count_65", Doc(new GetOutline { Path = [], First = 0, Count = 65 }, MessageType.GetOutline), Invalid);
        Req("outline_path_17_levels", Doc(new GetOutline { Path = new uint[17], First = 0, Count = 1 }, MessageType.GetOutline), Invalid);
        Req("outline_path_index_100000", Doc(new GetOutline { Path = [100000], First = 0, Count = 1 }, MessageType.GetOutline), Invalid);
        Req("outline_first_100000", Doc(new GetOutline { Path = [], First = 100000, Count = 1 }, MessageType.GetOutline), Invalid);
        Req("outline_without_a_document", RawFrame.Request(RawFrame.Serialized(new GetOutline { Path = [], First = 0, Count = 1 }), MessageType.GetOutline, 9, 0), Invalid);
        Req("page_links_count_513", Doc(new GetPageLinks { Page = 0, First = 0, Count = 513 }, MessageType.GetPageLinks), Invalid);
        Req("link_uri_index_100000", Doc(new GetLinkUri { Page = 0, Index = 100000 }, MessageType.GetLinkUri), Invalid);
        Req("page_boxes_page_10000000", Doc(new GetPageBoxes { Page = 10000000 }, MessageType.GetPageBoxes), Invalid);
        Req("page_text_count_2049", Doc(new GetPageText { Page = 0, First = 0, Count = 2049 }, MessageType.GetPageText), Invalid);
        Req("page_text_first_200000", Doc(new GetPageText { Page = 0, First = 200000, Count = 1 }, MessageType.GetPageText), Invalid);
        Req("search_query_513_bytes", Doc(new SearchPage { Page = 0, Query = new string('q', 513), Flags = 0 }, MessageType.SearchPage), Invalid);
        Req("search_query_with_nul", Doc(new SearchPage { Page = 0, Query = "a\0b", Flags = 0 }, MessageType.SearchPage), Invalid);
        Req("search_flags_unknown_bit", Doc(new SearchPage { Page = 0, Query = "q", Flags = 4 }, MessageType.SearchPage), Invalid);
        Req("search_query_invalid_utf8", RawFrame.Request(Items(0u, RawString(0xFF, 0xFE), 0u), MessageType.SearchPage, 9, 7), Protocol);

        // Replies. A hostile engine can send each of these.
        static byte[] Reply(object message, MessageType type) => RawFrame.Response(RawFrame.Serialized(message), type);
        Rsp("outline_total_100001", Reply(new GetOutlineReply { Total = 100001, Titles = ["a"], Kinds = [1], Pages = [0], Xs = [0f], Ys = [0f], HasChildren = [0] }, MessageType.GetOutline), Invalid);
        Rsp("outline_65_titles", Reply(new GetOutlineReply
        {
            Total = 65, Titles = [.. Enumerable.Repeat("t", 65)], Kinds = new ushort[65], Pages = new uint[65], Xs = new float[65], Ys = new float[65], HasChildren = new ushort[65],
        }, MessageType.GetOutline), Invalid);
        Rsp("outline_title_257_bytes", Reply(new GetOutlineReply { Total = 1, Titles = [new string('t', 257)], Kinds = [1], Pages = [0], Xs = [0f], Ys = [0f], HasChildren = [0] }, MessageType.GetOutline), Invalid);
        Rsp("outline_kind_6", Reply(new GetOutlineReply { Total = 1, Titles = ["a"], Kinds = [6], Pages = [0], Xs = [0f], Ys = [0f], HasChildren = [0] }, MessageType.GetOutline), Invalid);
        Rsp("outline_has_children_2", Reply(new GetOutlineReply { Total = 1, Titles = ["a"], Kinds = [1], Pages = [0], Xs = [0f], Ys = [0f], HasChildren = [2] }, MessageType.GetOutline), Invalid);
        Rsp("outline_page_10000000", Reply(new GetOutlineReply { Total = 1, Titles = ["a"], Kinds = [1], Pages = [10000000], Xs = [0f], Ys = [0f], HasChildren = [0] }, MessageType.GetOutline), Invalid);
        Rsp("outline_arrays_differ_in_length", Reply(new GetOutlineReply { Total = 1, Titles = ["a", "b"], Kinds = [1], Pages = [0], Xs = [0f], Ys = [0f], HasChildren = [0] }, MessageType.GetOutline), Invalid);
        Rsp("outline_y_out_of_range", Reply(new GetOutlineReply { Total = 1, Titles = ["a"], Kinds = [2], Pages = [0], Xs = [0f], Ys = [1000001f], HasChildren = [0] }, MessageType.GetOutline), Invalid);
        Rsp("outline_x_nan", RawFrame.Response(
            (ref MessagePackWriter w) =>
            {
                w.WriteArrayHeader(7); w.Write(1u);
                w.WriteArrayHeader(1); w.Write("a");
                w.WriteArrayHeader(1); w.Write((ushort)1);
                w.WriteArrayHeader(1); w.Write(0u);
                w.WriteArrayHeader(1); w.WriteRaw(RawNan);
                w.WriteArrayHeader(1); w.Write(0f);
                w.WriteArrayHeader(1); w.Write((ushort)0);
            },
            MessageType.GetOutline), Invalid);
        Rsp("links_total_100001", Reply(new GetPageLinksReply
        {
            Total = 100001, Lefts = [0f], Bottoms = [0f], Rights = [1f], Tops = [1f], Kinds = [1], Pages = [0], Xs = [0f], Ys = [0f],
        }, MessageType.GetPageLinks), Invalid);
        Rsp("links_kind_6", Reply(new GetPageLinksReply
        {
            Total = 1, Lefts = [0f], Bottoms = [0f], Rights = [1f], Tops = [1f], Kinds = [6], Pages = [0], Xs = [0f], Ys = [0f],
        }, MessageType.GetPageLinks), Invalid);
        Rsp("links_arrays_differ_in_length", Reply(new GetPageLinksReply
        {
            Total = 1, Lefts = [0f], Bottoms = [0f, 1f], Rights = [1f], Tops = [1f], Kinds = [1], Pages = [0], Xs = [0f], Ys = [0f],
        }, MessageType.GetPageLinks), Invalid);
        Rsp("links_left_out_of_range", Reply(new GetPageLinksReply
        {
            Total = 1, Lefts = [-1000001f], Bottoms = [0f], Rights = [1f], Tops = [1f], Kinds = [1], Pages = [0], Xs = [0f], Ys = [0f],
        }, MessageType.GetPageLinks), Invalid);
        Rsp("link_uri_2049_bytes", Reply(new GetLinkUriReply { Uri = new string('u', 2049) }, MessageType.GetLinkUri), Invalid);
        Rsp("link_uri_invalid_utf8", RawFrame.Response(Items(RawString(0xFF, 0xFE)), MessageType.GetLinkUri), Protocol);
        Rsp("page_boxes_rotation_four", Reply(new GetPageBoxesReply { MediaRight = 612f, MediaTop = 792f, CropRight = 612f, CropTop = 792f, HasCropBox = true, Rotation = 4 }, MessageType.GetPageBoxes), Invalid);
        Rsp("page_boxes_infinite", RawFrame.Response(
            (ref MessagePackWriter w) =>
            {
                w.WriteArrayHeader(10);
                w.WriteRaw(RawInf); w.Write(0f); w.Write(612f); w.Write(792f);
                w.Write(false);
                w.Write(0f); w.Write(0f); w.Write(0f); w.Write(0f);
                w.Write((ushort)0);
            },
            MessageType.GetPageBoxes), Invalid);
        Rsp("page_text_total_200001", Reply(new GetPageTextReply
        {
            Total = 200001, Units = [65], Lefts = [0f], Bottoms = [0f], Rights = [1f], Tops = [1f], Flags = [0],
        }, MessageType.GetPageText), Invalid);
        Rsp("page_text_flags_4", Reply(new GetPageTextReply
        {
            Total = 1, Units = [65], Lefts = [0f], Bottoms = [0f], Rights = [1f], Tops = [1f], Flags = [4],
        }, MessageType.GetPageText), Invalid);
        Rsp("page_text_unit_70000", RawFrame.Response(
            (ref MessagePackWriter w) =>
            {
                w.WriteArrayHeader(8); w.Write(1u); w.Write(false);
                w.WriteArrayHeader(1); w.Write(70000u);
                for (var i = 0; i < 4; i++) { w.WriteArrayHeader(1); w.Write(0f); }
                w.WriteArrayHeader(1); w.Write((ushort)0);
            },
            MessageType.GetPageText), Protocol);
        Rsp("page_text_2049_units", Reply(new GetPageTextReply
        {
            Total = 2049, Units = new ushort[2049], Lefts = new float[2049], Bottoms = new float[2049], Rights = new float[2049], Tops = new float[2049], Flags = new ushort[2049],
        }, MessageType.GetPageText), Invalid);
        Rsp("page_text_arrays_differ_in_length", Reply(new GetPageTextReply
        {
            Total = 2, Units = [65, 66], Lefts = [0f], Bottoms = [0f, 0f], Rights = [1f, 1f], Tops = [1f, 1f], Flags = [0, 0],
        }, MessageType.GetPageText), Invalid);
        Rsp("search_length_zero", Reply(new SearchPageReply { Truncated = false, Starts = [0], Lengths = [0] }, MessageType.SearchPage), Invalid);
        Rsp("search_start_200000", Reply(new SearchPageReply { Truncated = false, Starts = [200000], Lengths = [1] }, MessageType.SearchPage), Invalid);
        Rsp("search_2049_matches", Reply(new SearchPageReply { Truncated = true, Starts = new uint[2049], Lengths = Enumerable.Repeat(1u, 2049).ToArray() }, MessageType.SearchPage), Invalid);

        // GetDocumentDetails (schema 4).
        Req("document_details_without_a_document", RawFrame.Request(Items(), MessageType.GetDocumentDetails, 20, 0), Invalid);
        Req("document_details_with_a_field", RawFrame.Request(Items(1u), MessageType.GetDocumentDetails, 20, 7), Protocol);
        Rsp("details_date_65_bytes", Reply(new GetDocumentDetailsReply { CreationDate = new string('d', 65), ModificationDate = "", FontNames = [], FontFlags = [] }, MessageType.GetDocumentDetails), Invalid);
        Rsp("details_modification_date_65_bytes", Reply(new GetDocumentDetailsReply { CreationDate = "", ModificationDate = new string('d', 65), FontNames = [], FontFlags = [] }, MessageType.GetDocumentDetails), Invalid);
        Rsp("details_revision_256", Reply(new GetDocumentDetailsReply { CreationDate = "", ModificationDate = "", SecurityRevision = 256, FontNames = [], FontFlags = [] }, MessageType.GetDocumentDetails), Invalid);
        Rsp("details_form_type_4", Reply(new GetDocumentDetailsReply { CreationDate = "", ModificationDate = "", FormType = 4, FontNames = [], FontFlags = [] }, MessageType.GetDocumentDetails), Invalid);
        Rsp("details_65_fonts", Reply(new GetDocumentDetailsReply
        {
            CreationDate = "", ModificationDate = "", FontNames = [.. Enumerable.Repeat("F", 65)], FontFlags = new ushort[65],
        }, MessageType.GetDocumentDetails), Invalid);
        Rsp("details_font_name_129_bytes", Reply(new GetDocumentDetailsReply { CreationDate = "", ModificationDate = "", FontNames = [new string('f', 129)], FontFlags = [0] }, MessageType.GetDocumentDetails), Invalid);
        Rsp("details_font_flags_2", Reply(new GetDocumentDetailsReply { CreationDate = "", ModificationDate = "", FontNames = ["F"], FontFlags = [2] }, MessageType.GetDocumentDetails), Invalid);
        Rsp("details_font_arrays_differ_in_length", Reply(new GetDocumentDetailsReply { CreationDate = "", ModificationDate = "", FontNames = ["A", "B"], FontFlags = [0] }, MessageType.GetDocumentDetails), Invalid);
        Rsp("details_pages_scanned_10000001", Reply(new GetDocumentDetailsReply { CreationDate = "", ModificationDate = "", FontNames = [], FontFlags = [], FontPagesScanned = 10000001 }, MessageType.GetDocumentDetails), Invalid);
        Rsp("details_date_invalid_utf8", RawFrame.Response(
            (ref MessagePackWriter w) =>
            {
                w.WriteArrayHeader(9);
                w.WriteRaw(RawString(0xFF, 0xFE)); w.Write("");
                w.Write(0u); w.Write(0u); w.Write(0u);
                w.WriteArrayHeader(0); w.WriteArrayHeader(0);
                w.Write(true); w.Write(0u);
            },
            MessageType.GetDocumentDetails), Protocol);
        Rsp("details_font_name_nil", RawFrame.Response(
            (ref MessagePackWriter w) =>
            {
                w.WriteArrayHeader(9);
                w.Write(""); w.Write("");
                w.Write(0u); w.Write(0u); w.Write(0u);
                w.WriteArrayHeader(1); w.WriteNil();
                w.WriteArrayHeader(1); w.Write((ushort)0);
                w.Write(true); w.Write(0u);
            },
            MessageType.GetDocumentDetails), Protocol);

        // Schema 5. Requests are hand-made with the real writer, which does not validate; "none" messages carry no document, the others document 7.
        static byte[] NoDoc(object message, MessageType type) => RawFrame.Request(RawFrame.Serialized(message), type, 9, 0, 1000);
        static RenderBand Band(Action<RenderBand> edit)
        {
            var band = new RenderBand { Page = 0, Dpi = 150, Rotation = 0, PageWidthPx = 1275, PageHeightPx = 1650, BandTop = 0, BandHeight = 256, Flags = 5, SlotIndex = 1, Generation = 1 };
            edit(band);
            return band;
        }

        // The shell session (ADR-0018): ShellOpen carries no document, the rest carry the session's documentId (3 here).
        static ShellRenderPage Shell(Action<ShellRenderPage> edit)
        {
            var render = new ShellRenderPage { Page = 0, MaxWidth = 256, MaxHeight = 256, Flags = 0, Pixels = 1, PixelBytes = 262144 };
            edit(render);
            return render;
        }

        static byte[] InSession(object message, MessageType type, uint documentId = 3) => RawFrame.Request(RawFrame.Serialized(message), type, 9, documentId, 1000);

        Req("viewer_preferences_without_a_document", RawFrame.Request(Items(), MessageType.GetViewerPreferences, 21, 0), Invalid);
        Req("viewer_preferences_with_a_field", RawFrame.Request(Items(1u), MessageType.GetViewerPreferences, 21, 7), Protocol);
        Req("render_band_without_a_document", RawFrame.Request(RawFrame.Serialized(Band(_ => { })), MessageType.RenderBand, 22, 0), Invalid);
        Req("render_band_dpi_71", Doc(Band(b => b.Dpi = 71), MessageType.RenderBand), Invalid);
        Req("render_band_dpi_1201", Doc(Band(b => b.Dpi = 1201), MessageType.RenderBand), Invalid);
        Req("render_band_rotation_four", Doc(Band(b => b.Rotation = 4), MessageType.RenderBand), Invalid);
        Req("render_band_page_width_zero", Doc(Band(b => b.PageWidthPx = 0), MessageType.RenderBand), Invalid);
        Req("render_band_page_width_65537", Doc(Band(b => b.PageWidthPx = 65537), MessageType.RenderBand), Invalid);
        Req("render_band_page_height_zero", Doc(Band(b => b.PageHeightPx = 0), MessageType.RenderBand), Invalid);
        Req("render_band_page_height_65537", Doc(Band(b => b.PageHeightPx = 65537), MessageType.RenderBand), Invalid);
        Req("render_band_top_65536", Doc(Band(b => b.BandTop = 65536), MessageType.RenderBand), Invalid);
        Req("render_band_height_zero", Doc(Band(b => b.BandHeight = 0), MessageType.RenderBand), Invalid);
        Req("render_band_height_16385", Doc(Band(b => b.BandHeight = 16385), MessageType.RenderBand), Invalid);
        Req("render_band_reserved_flag_bit", Doc(Band(b => b.Flags = 8), MessageType.RenderBand), Invalid);
        Req("render_band_slot_1024", Doc(Band(b => b.SlotIndex = 1024), MessageType.RenderBand), Invalid);
        Req("render_band_page_10000000", Doc(Band(b => b.Page = 10000000), MessageType.RenderBand), Invalid);
        Req("render_band_dpi_is_a_float", RawFrame.Request(Items(0u, 150.5f, (ushort)0, 1275u, 1650u, 0u, 256u, 5u, 1u, 1UL), MessageType.RenderBand, 22, 7), Protocol);

        Req("shell_open_without_a_section", NoDoc(new ShellOpen { Section = 0, Bytes = 1 }, MessageType.ShellOpen), Invalid);
        Req("shell_open_zero_bytes", NoDoc(new ShellOpen { Section = 1, Bytes = 0 }, MessageType.ShellOpen), Invalid);
        Req("shell_open_bytes_above_32_mib", NoDoc(new ShellOpen { Section = 1, Bytes = 33_554_433 }, MessageType.ShellOpen), Invalid);
        Req("shell_open_with_a_document", RawFrame.Request(RawFrame.Serialized(new ShellOpen { Section = 1, Bytes = 1 }), MessageType.ShellOpen, 9, 7), Invalid);
        Req("shell_open_with_a_password", RawFrame.Request(Items(1UL, 1u, "secret"), MessageType.ShellOpen, 9, 0), Protocol); // a shell never prompts: there is no such field
        Req("shell_render_page_without_a_session", RawFrame.Request(RawFrame.Serialized(Shell(_ => { })), MessageType.ShellRenderPage, 9, 0), Invalid);
        Req("shell_render_page_width_15", InSession(Shell(r => r.MaxWidth = 15), MessageType.ShellRenderPage), Invalid);
        Req("shell_render_page_width_1025", InSession(Shell(r => r.MaxWidth = 1025), MessageType.ShellRenderPage), Invalid);
        Req("shell_render_page_height_15", InSession(Shell(r => r.MaxHeight = 15), MessageType.ShellRenderPage), Invalid);
        Req("shell_render_page_height_1025", InSession(Shell(r => r.MaxHeight = 1025), MessageType.ShellRenderPage), Invalid);
        Req("shell_render_page_flags_2", InSession(Shell(r => r.Flags = 2), MessageType.ShellRenderPage), Invalid);
        Req("shell_render_page_pixels_zero", InSession(Shell(r => r.Pixels = 0), MessageType.ShellRenderPage), Invalid);
        Req("shell_render_page_pixel_bytes_3", InSession(Shell(r => r.PixelBytes = 3), MessageType.ShellRenderPage), Invalid);
        Req("shell_render_page_pixel_bytes_4_mib_plus_1", InSession(Shell(r => r.PixelBytes = 4_194_305), MessageType.ShellRenderPage), Invalid);
        Req("shell_render_page_page_10000000", InSession(Shell(r => r.Page = 10000000), MessageType.ShellRenderPage), Invalid);
        Req("shell_close_without_a_session", NoDoc(new ShellClose(), MessageType.ShellClose), Invalid);
        Req("shell_close_with_a_field", RawFrame.Request(Items(1u), MessageType.ShellClose, 9, 3), Protocol);


        Rsp("pong_without_progress", RawFrame.Response(Items(1UL), MessageType.Ping), Protocol); // the schema 4 layout
        Rsp("pong_progress_is_a_float", RawFrame.Response(Items(1UL, 1.5f), MessageType.Ping), Protocol);
        Rsp("pong_progress_is_negative", RawFrame.Response(Items(1UL, -1), MessageType.Ping), Protocol);
        Rsp("viewer_preferences_direction_3", Reply(new GetViewerPreferencesReply { Direction = 3, RangeFirsts = [], RangeLasts = [] }, MessageType.GetViewerPreferences), Invalid);
        Rsp("viewer_preferences_print_scaling_3", Reply(new GetViewerPreferencesReply { PrintScaling = 3, RangeFirsts = [], RangeLasts = [] }, MessageType.GetViewerPreferences), Invalid);
        Rsp("viewer_preferences_copies_100", Reply(new GetViewerPreferencesReply { NumCopies = 100, RangeFirsts = [], RangeLasts = [] }, MessageType.GetViewerPreferences), Invalid);
        Rsp("viewer_preferences_duplex_4", Reply(new GetViewerPreferencesReply { Duplex = 4, RangeFirsts = [], RangeLasts = [] }, MessageType.GetViewerPreferences), Invalid);
        Rsp("viewer_preferences_65_ranges", Reply(new GetViewerPreferencesReply { RangeFirsts = new uint[65], RangeLasts = new uint[65] }, MessageType.GetViewerPreferences), Invalid);
        Rsp("viewer_preferences_range_page_10000000", Reply(new GetViewerPreferencesReply { RangeFirsts = [0], RangeLasts = [10000000] }, MessageType.GetViewerPreferences), Invalid);
        Rsp("viewer_preferences_ranges_differ_in_length", Reply(new GetViewerPreferencesReply { RangeFirsts = [0, 1], RangeLasts = [2] }, MessageType.GetViewerPreferences), Invalid);
        Rsp("render_band_reply_width_zero", Reply(new RenderBandReply { SlotIndex = 0, Width = 0, Height = 1, Stride = 4, Generation = 0 }, MessageType.RenderBand), Invalid);
        Rsp("render_band_reply_width_65537", Reply(new RenderBandReply { SlotIndex = 0, Width = 65537, Height = 1, Stride = 4, Generation = 0 }, MessageType.RenderBand), Invalid);
        Rsp("render_band_reply_height_16385", Reply(new RenderBandReply { SlotIndex = 0, Width = 1, Height = 16385, Stride = 4, Generation = 0 }, MessageType.RenderBand), Invalid);
        Rsp("render_band_reply_stride_3", Reply(new RenderBandReply { SlotIndex = 0, Width = 1, Height = 1, Stride = 3, Generation = 0 }, MessageType.RenderBand), Invalid);
        Rsp("render_band_reply_stride_262145", Reply(new RenderBandReply { SlotIndex = 0, Width = 1, Height = 1, Stride = 262145, Generation = 0 }, MessageType.RenderBand), Invalid);
        Rsp("render_band_reply_slot_1024", Reply(new RenderBandReply { SlotIndex = 1024, Width = 1, Height = 1, Stride = 4, Generation = 0 }, MessageType.RenderBand), Invalid);
        Rsp("shell_open_reply_document_zero", Reply(new ShellOpenReply { DocumentId = 0, PageCount = 1, FirstWidth = 612f, FirstHeight = 792f }, MessageType.ShellOpen), Invalid);
        Rsp("shell_open_reply_pages_10000001", Reply(new ShellOpenReply { DocumentId = 1, PageCount = 10000001, FirstWidth = 612f, FirstHeight = 792f }, MessageType.ShellOpen), Invalid);
        Rsp("shell_open_reply_width_zero", Reply(new ShellOpenReply { DocumentId = 1, PageCount = 1, FirstWidth = 0f, FirstHeight = 792f }, MessageType.ShellOpen), Invalid);
        Rsp("shell_open_reply_height_14401", Reply(new ShellOpenReply { DocumentId = 1, PageCount = 1, FirstWidth = 612f, FirstHeight = 14401f }, MessageType.ShellOpen), Invalid);
        Rsp("shell_open_reply_width_infinite", RawFrame.Response(Items(1u, 1u, RawInf, 792f), MessageType.ShellOpen), Invalid);
        Rsp("shell_open_reply_width_nan", RawFrame.Response(Items(1u, 1u, RawNan, 792f), MessageType.ShellOpen), Invalid);
        Rsp("shell_render_page_reply_width_1025", Reply(new ShellRenderPageReply { Width = 1025, Height = 1, Stride = 4 }, MessageType.ShellRenderPage), Invalid);
        Rsp("shell_render_page_reply_height_zero", Reply(new ShellRenderPageReply { Width = 1, Height = 0, Stride = 4 }, MessageType.ShellRenderPage), Invalid);
        Rsp("shell_render_page_reply_height_1025", Reply(new ShellRenderPageReply { Width = 1, Height = 1025, Stride = 4 }, MessageType.ShellRenderPage), Invalid);
        Rsp("shell_render_page_reply_stride_3", Reply(new ShellRenderPageReply { Width = 1, Height = 1, Stride = 3 }, MessageType.ShellRenderPage), Invalid);
        Rsp("shell_render_page_reply_stride_4097", Reply(new ShellRenderPageReply { Width = 1, Height = 1, Stride = 4097 }, MessageType.ShellRenderPage), Invalid);
        Rsp("shell_close_reply_with_a_field", RawFrame.Response(Items(1u), MessageType.ShellClose), Protocol);

        // Schema 6: the writer host's save messages. "none" messages carry no document.
        static AppendAnnotations Append(Action<AppendAnnotations> edit)
        {
            var message = new AppendAnnotations { BaseHandle = 1, EditsHandle = 2, EditsLength = 100, OutputHandle = 3, PageCount = 2 };
            edit(message);
            return message;
        }

        Req("append_annotations_base_handle_zero", NoDoc(Append(m => m.BaseHandle = 0), MessageType.AppendAnnotations), Invalid);
        Req("append_annotations_edits_handle_zero", NoDoc(Append(m => m.EditsHandle = 0), MessageType.AppendAnnotations), Invalid);
        Req("append_annotations_output_handle_zero", NoDoc(Append(m => m.OutputHandle = 0), MessageType.AppendAnnotations), Invalid);
        Req("append_annotations_edits_length_zero", NoDoc(Append(m => m.EditsLength = 0), MessageType.AppendAnnotations), Invalid);
        Req("append_annotations_edits_above_256_mib", NoDoc(Append(m => m.EditsLength = 268_435_457), MessageType.AppendAnnotations), Invalid);
        Req("append_annotations_pages_10000001", NoDoc(Append(m => m.PageCount = 10_000_001), MessageType.AppendAnnotations), Invalid);
        Req("append_annotations_with_a_document", RawFrame.Request(RawFrame.Serialized(Append(_ => { })), MessageType.AppendAnnotations, 9, 7), Invalid);
        Req("append_annotations_with_a_path", RawFrame.Request(Items(1UL, 2UL, 100UL, 3UL, 2u, "C:\\a.pdf"), MessageType.AppendAnnotations, 9, 0), Protocol); // handles only, never paths
        Req("append_annotations_handle_is_a_string", RawFrame.Request(Items("1", 2UL, 100UL, 3UL, 2u), MessageType.AppendAnnotations, 9, 0), Protocol);
        Req("rewrite_document_base_handle_zero", NoDoc(new RewriteDocument { BaseHandle = 0, OutputHandle = 1, Flags = 0 }, MessageType.RewriteDocument), Invalid);
        Req("rewrite_document_output_handle_zero", NoDoc(new RewriteDocument { BaseHandle = 1, OutputHandle = 0, Flags = 0 }, MessageType.RewriteDocument), Invalid);
        Req("rewrite_document_flags_reserved_bit", NoDoc(new RewriteDocument { BaseHandle = 1, OutputHandle = 2, Flags = 4 }, MessageType.RewriteDocument), Invalid);
        Req("rewrite_document_with_a_document", RawFrame.Request(RawFrame.Serialized(new RewriteDocument { BaseHandle = 1, OutputHandle = 2, Flags = 0 }), MessageType.RewriteDocument, 9, 7), Invalid);
        Req("verify_annotations_base_handle_zero", NoDoc(new VerifyAnnotations { BaseHandle = 0, OutputHandle = 1, PageCount = 1, Pages = [], Added = [], Removed = [] }, MessageType.VerifyAnnotations), Invalid);
        Req("verify_annotations_2049_pages", NoDoc(new VerifyAnnotations { BaseHandle = 1, OutputHandle = 2, PageCount = 5000, Pages = new uint[2049], Added = new uint[2049], Removed = new uint[2049] }, MessageType.VerifyAnnotations), Invalid);
        Req("verify_annotations_page_10000000", NoDoc(new VerifyAnnotations { BaseHandle = 1, OutputHandle = 2, PageCount = 1, Pages = [10_000_000], Added = [0], Removed = [0] }, MessageType.VerifyAnnotations), Invalid);
        Req("verify_annotations_counts_differ_in_length", NoDoc(new VerifyAnnotations { BaseHandle = 1, OutputHandle = 2, PageCount = 1, Pages = [0, 1], Added = [0], Removed = [0, 0] }, MessageType.VerifyAnnotations), Invalid);
        Req("verify_annotations_count_above_100_million", NoDoc(new VerifyAnnotations { BaseHandle = 1, OutputHandle = 2, PageCount = 1, Pages = [0], Added = [100_000_001], Removed = [0] }, MessageType.VerifyAnnotations), Invalid);
        Rsp("append_annotations_reply_257_issues", RawFrame.Response(
            (ref MessagePackWriter w) =>
            {
                w.WriteArrayHeader(8); w.Write(false); w.Write(0u); w.Write(0u); w.Write(0u); w.Write(0UL); w.Write(false);
                w.WriteArrayHeader(257);
                for (var i = 0; i < 257; i++) w.Write((uint)i);
                w.WriteArrayHeader(0);
            },
            MessageType.AppendAnnotations), Invalid);
        Rsp("append_annotations_reply_bytes_above_2_gib", Reply(new AppendAnnotationsReply { Ok = true, BytesWritten = 2_147_483_648UL, IssueCodes = [], IssueMessages = [] }, MessageType.AppendAnnotations), Invalid);
        Rsp("append_annotations_reply_added_above_100_million", Reply(new AppendAnnotationsReply { Ok = true, AnnotationsAdded = 100_000_001, IssueCodes = [], IssueMessages = [] }, MessageType.AppendAnnotations), Invalid);
        Rsp("append_annotations_reply_issue_codes_differ_in_length", Reply(new AppendAnnotationsReply { Ok = false, IssueCodes = [1, 2], IssueMessages = ["one"] }, MessageType.AppendAnnotations), Invalid);
        Rsp("append_annotations_reply_message_193_bytes", Reply(new AppendAnnotationsReply { Ok = false, IssueCodes = [1], IssueMessages = [new string('m', 193)] }, MessageType.AppendAnnotations), Invalid);
        Rsp("rewrite_document_reply_flattened_above_100_million", Reply(new RewriteDocumentReply { Ok = true, AnnotationsFlattened = 100_000_001, IssueCodes = [], IssueMessages = [] }, MessageType.RewriteDocument), Invalid);
        Rsp("rewrite_document_reply_pages_10000001", Reply(new RewriteDocumentReply { Ok = true, PagesChanged = 10_000_001, IssueCodes = [], IssueMessages = [] }, MessageType.RewriteDocument), Invalid);
        Rsp("verify_annotations_reply_2049_mismatches", Reply(new VerifyAnnotationsReply { Ok = false, Mismatches = new uint[2049] }, MessageType.VerifyAnnotations), Invalid);
        Rsp("verify_annotations_reply_mismatch_page_10000000", Reply(new VerifyAnnotationsReply { Ok = false, Mismatches = [10_000_000] }, MessageType.VerifyAnnotations), Invalid);
        Rsp("verify_annotations_reply_extra_field", RawFrame.Response(Items(true, 0u, 5u), MessageType.VerifyAnnotations), Protocol);

        return v;
    }
}
