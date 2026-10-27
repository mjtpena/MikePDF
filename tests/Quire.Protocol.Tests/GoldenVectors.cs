using MessagePack;

namespace Quire.Protocol.Tests;

/// <summary>A frame that must be rejected, and the status both decoders must give.</summary>
public sealed record InvalidVector(string Name, bool IsRequest, byte[] Frame, Status Expected);

/// <summary>
/// The golden frames: every valid sample plus a set of malformed ones, each malformed in exactly one way. The C# tests
/// write them to tests/Quire.Protocol.Tests/golden (set QUIRE_UPDATE_GOLDEN=1), and the C++ tests read the same files.
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
        Req("flags_unknown_bit", TileWith(7, 4u), Invalid);
        Req("handle_zero", RawFrame.Request(Items(0UL, null!), MessageType.OpenDocument), Invalid);
        Req("password_1025_bytes", RawFrame.Request(Items(1UL, new string('a', 1025)), MessageType.OpenDocument), Invalid);
        Req("password_invalid_utf8", RawFrame.Request(Items(1UL, RawString(0xFF, 0xFE)), MessageType.OpenDocument), Protocol);
        Req("password_overlong_utf8", RawFrame.Request(Items(1UL, RawString(0xC0, 0x80)), MessageType.OpenDocument), Protocol);
        Req("password_with_nul", RawFrame.Request(Items(1UL, "a\0b"), MessageType.OpenDocument), Invalid);
        Req("hello_undefined_process_kind", RawFrame.Request(Items(1u, (ushort)3, 1u), MessageType.Hello), Invalid);
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

        return v;
    }
}
