using System.Buffers.Binary;
using MessagePack;

namespace Quire.Protocol.Tests;

/// <summary>Framing and wire-type failures. Limit failures are covered, field by field, in LimitTests.</summary>
public sealed class MalformedFrameTests
{
    private static byte[] Ping() => ProtocolCodec.EncodeRequestFrame(1, 0, 0, new Ping { Nonce = 5 }).Value;

    [Fact]
    public void An_oversize_length_prefix_is_refused_before_any_body_is_read()
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, ProtocolInfo.MaxFrameBytes + 1u);
        var failure = ProtocolCodec.ReadFrameLength(header, out _);
        Assert.Equal(Status.ProtocolError, failure!.Value.Status);

        BinaryPrimitives.WriteUInt32LittleEndian(header, uint.MaxValue);
        Assert.NotNull(ProtocolCodec.ReadFrameLength(header, out _));
    }

    [Fact]
    public void The_length_limit_is_inclusive()
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, ProtocolInfo.MaxFrameBytes);
        Assert.Null(ProtocolCodec.ReadFrameLength(header, out var length));
        Assert.Equal(ProtocolInfo.MaxFrameBytes, length);
        BinaryPrimitives.WriteUInt32LittleEndian(header, 1);
        Assert.Null(ProtocolCodec.ReadFrameLength(header, out length));
        Assert.Equal(1, length);
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0);
        Assert.NotNull(ProtocolCodec.ReadFrameLength(header, out _));
    }

    [Fact]
    public void A_full_size_frame_of_garbage_is_rejected_not_crashed_on()
    {
        var body = new byte[ProtocolInfo.MaxFrameBytes];
        Array.Fill(body, (byte)0xDD);
        var frame = RawFrame.Prefix(body);
        Assert.Equal(Status.ProtocolError, ProtocolCodec.DecodeRequestFrame(frame).Status);
        Assert.Equal(Status.ProtocolError, ProtocolCodec.DecodeResponseFrame(frame).Status);

        var tooBig = RawFrame.Prefix(new byte[ProtocolInfo.MaxFrameBytes + 1]);
        Assert.Equal(Status.ProtocolError, ProtocolCodec.DecodeRequestFrame(tooBig).Status);
    }

    [Fact]
    public void Every_truncation_of_every_valid_frame_is_rejected()
    {
        var frames = Samples.Requests.Select(r => (Request: true, Bytes: ProtocolCodec.EncodeRequestFrame(r.RequestId, r.DocumentId, r.DeadlineMs, r.Payload).Value))
            .Concat(Samples.Responses.Select(r => (Request: false, Bytes: ProtocolCodec.EncodeResponseFrame(r.RequestId, r.Status, r.Type, r.Payload).Value)));
        foreach (var (request, bytes) in frames)
        {
            for (var keep = 0; keep < bytes.Length; keep++)
            {
                var cut = bytes[..keep];
                var status = request ? ProtocolCodec.DecodeRequestFrame(cut).Status : ProtocolCodec.DecodeResponseFrame(cut).Status;
                Assert.Equal(Status.ProtocolError, status);

                // Also truncated inside the body while the prefix still claims the full length.
                if (keep > 4)
                {
                    var lying = RawFrame.Prefix(bytes[4..keep], (uint)(bytes.Length - 4));
                    var s2 = request ? ProtocolCodec.DecodeRequestFrame(lying).Status : ProtocolCodec.DecodeResponseFrame(lying).Status;
                    Assert.Equal(Status.ProtocolError, s2);
                }

                // And a body that ends early under an honest prefix.
                if (keep > 4)
                {
                    var honest = RawFrame.Prefix(bytes[4..keep]);
                    var s3 = request ? ProtocolCodec.DecodeRequestFrame(honest).Status : ProtocolCodec.DecodeResponseFrame(honest).Status;
                    Assert.NotEqual(Status.Ok, s3);
                }
            }
        }
    }

    [Fact]
    public void Extra_bytes_after_a_frame_or_inside_the_body_are_rejected()
    {
        var ping = Ping();
        byte[] extra = [.. ping, 0];
        Assert.Equal(Status.ProtocolError, ProtocolCodec.DecodeRequestFrame(extra).Status);
        Assert.Equal(Status.ProtocolError, ProtocolCodec.DecodeRequestFrame(RawFrame.Prefix([.. ping[4..], 0xC0])).Status);
        Assert.Equal(Status.ProtocolError, ProtocolCodec.DecodeRequestFrame(RawFrame.Prefix([.. ping[4..], .. ping[4..]])).Status);
    }

    [Fact]
    public void Deeply_nested_input_is_rejected_without_exhausting_the_stack()
    {
        foreach (var depth in new[] { 5, 100, 10_000, 30_000 })
        {
            var frame = RawFrame.Request((ref MessagePackWriter w) =>
            {
                for (var i = 0; i < depth; i++) w.WriteArrayHeader(1);
                w.Write(1u);
            }, MessageType.Ping);
            Assert.Equal(Status.ProtocolError, ProtocolCodec.DecodeRequestFrame(frame).Status);
        }

        // Nested where an array of scalars is expected, and in a response header position.
        var nestedElements = RawFrame.Response((ref MessagePackWriter w) =>
        {
            w.WriteArrayHeader(3);
            w.WriteArrayHeader(1);
            for (var i = 0; i < 20_000; i++) w.WriteArrayHeader(1);
        }, MessageType.GetPageSizes);
        Assert.Equal(Status.ProtocolError, ProtocolCodec.DecodeResponseFrame(nestedElements).Status);
    }

    [Fact]
    public void A_count_or_length_larger_than_the_message_is_refused_before_anything_is_allocated()
    {
        byte[][] huge =
        [
            [0xDD, 0xFF, 0xFF, 0xFF, 0xFF], // array32, 4 Gi - 1 elements
            [0xDD, 0x80, 0x00, 0x00, 0x00], // array32, 2 Gi elements: not a valid int count
            [0xDD, 0x00, 0x0F, 0x42, 0x40],
            [0xDC, 0xFF, 0xFF],
        ];
        foreach (var header in huge)
        {
            var asWidths = RawFrame.Response((ref MessagePackWriter w) =>
            {
                w.WriteArrayHeader(3);
                w.WriteRaw(header);
            }, MessageType.GetPageSizes);
            Assert.Equal(Status.ProtocolError, ProtocolCodec.DecodeResponseFrame(asWidths).Status);

            var asPayload = RawFrame.Request((ref MessagePackWriter w) => w.WriteRaw(header), MessageType.Ping);
            Assert.Equal(Status.ProtocolError, ProtocolCodec.DecodeRequestFrame(asPayload).Status);
        }

        byte[][] hugeStrings =
        [
            [0xDB, 0xFF, 0xFF, 0xFF, 0xFF, 0x61],
            [0xDB, 0x80, 0x00, 0x00, 0x00, 0x61],
            [0xDA, 0xFF, 0xFF, 0x61],
            [0xD9, 0xFF, 0x61],
            [0xBF, 0x61],
        ];
        foreach (var header in hugeStrings)
        {
            var frame = RawFrame.Request((ref MessagePackWriter w) =>
            {
                w.WriteArrayHeader(2);
                w.Write(1UL);
                w.WriteRaw(header);
            }, MessageType.OpenDocument);
            Assert.Equal(Status.ProtocolError, ProtocolCodec.DecodeRequestFrame(frame).Status);
        }
    }

    [Fact]
    public void Maps_and_extension_types_are_rejected()
    {
        var map = RawFrame.Request((ref MessagePackWriter w) => w.WriteMapHeader(0), MessageType.Ping);
        Assert.Equal(Status.ProtocolError, ProtocolCodec.DecodeRequestFrame(map).Status);
        var ext = RawFrame.Request((ref MessagePackWriter w) =>
        {
            w.WriteArrayHeader(1);
            w.WriteExtensionFormat(new ExtensionResult(1, new byte[] { 1 }));
        }, MessageType.Ping);
        Assert.Equal(Status.ProtocolError, ProtocolCodec.DecodeRequestFrame(ext).Status);
    }

    [Fact]
    public void Integers_are_accepted_in_any_wire_width_when_the_value_fits()
    {
        // 5 as uint8, uint16, uint32, uint64, int8, int16, int32, int64: all decode to the same nonce.
        byte[][] encodings =
        [
            [0xCC, 5], [0xCD, 0, 5], [0xCE, 0, 0, 0, 5], [0xCF, 0, 0, 0, 0, 0, 0, 0, 5],
            [0xD0, 5], [0xD1, 0, 5], [0xD2, 0, 0, 0, 5], [0xD3, 0, 0, 0, 0, 0, 0, 0, 5],
        ];
        foreach (var enc in encodings)
        {
            var frame = RawFrame.Request((ref MessagePackWriter w) =>
            {
                w.WriteArrayHeader(1);
                w.WriteRaw(enc);
            }, MessageType.Ping);
            var decoded = ProtocolCodec.DecodeRequestFrame(frame);
            Assert.True(decoded.IsOk, Convert.ToHexString(enc));
            Assert.Equal(5UL, ((Ping)decoded.Value.Payload).Nonce);
        }
    }

    [Fact]
    public void A_value_too_wide_for_its_type_is_a_protocol_error_not_an_overflow()
    {
        // Rotation is u16: 65536 does not fit, however it is spelled.
        foreach (var raw in new byte[][] { [0xCE, 0, 1, 0, 0], [0xCF, 0, 0, 0, 0, 0, 1, 0, 0], [0xFF], [0xD0, 0x80], [0xD3, 0x80, 0, 0, 0, 0, 0, 0, 0] })
        {
            var frame = RawFrame.Request((ref MessagePackWriter w) =>
            {
                w.WriteArrayHeader(10);
                w.Write(0u); w.Write(1f); w.WriteRaw(raw); w.Write(0); w.Write(0); w.Write(1u); w.Write(1u); w.Write(0u); w.Write(0u); w.Write(0UL);
            }, MessageType.RenderTile, 1, 1);
            Assert.Equal(Status.ProtocolError, ProtocolCodec.DecodeRequestFrame(frame).Status);
        }
    }

    [Fact]
    public void Response_envelope_rules()
    {
        // The reply payload type follows the messageType in the response, not anything the sender has to guess.
        var ok = ProtocolCodec.EncodeResponseFrame(1, Status.Ok, MessageType.Ping, new Pong { Nonce = 3 }).Value;
        Assert.Equal(MessageType.Ping, ProtocolCodec.DecodeResponseFrame(ok).Value.MessageType);

        // Status Ok must carry a payload array, not nil.
        Assert.Equal(Status.ProtocolError, ProtocolCodec.DecodeResponseFrame(RawFrame.Response(null, MessageType.Ping)).Status);
        // An error status must carry nil, not an array.
        Assert.Equal(Status.ProtocolError, ProtocolCodec.DecodeResponseFrame(RawFrame.Response((ref MessagePackWriter w) => w.WriteArrayHeader(0), MessageType.Shutdown, Status.Cancelled)).Status);
        // PartiallyRendered carries a payload.
        Assert.True(ProtocolCodec.DecodeResponseFrame(RawFrame.Response((ref MessagePackWriter w) =>
        {
            w.WriteArrayHeader(5); w.Write(0u); w.Write(1u); w.Write(1u); w.Write(4u); w.Write(1UL);
        }, MessageType.RenderTile, Status.PartiallyRendered)).IsOk);
    }

    [Fact]
    public void Unknown_enum_values_in_the_response_header_are_protocol_errors()
    {
        foreach (var status in new ushort[] { 13, 255, 65535 })
            Assert.Equal(Status.ProtocolError, ProtocolCodec.DecodeResponseFrame(RawFrame.Response(null, MessageType.Ping, (Status)status)).Status);
        foreach (var type in new ushort[] { 0, 4, 15, 26, 1023, 1025, 65535 })
            Assert.Equal(Status.ProtocolError, ProtocolCodec.DecodeRequestFrame(RawFrame.Request((ref MessagePackWriter w) => w.WriteArrayHeader(0), (MessageType)type)).Status);
    }

    [Fact]
    public void The_schema_version_is_carried_in_Hello_and_is_not_judged_by_the_codec()
    {
        // The codec accepts any version in range so the broker can see a mismatch and kill the peer deliberately.
        var other = ProtocolCodec.EncodeRequestFrame(1, 0, 0, new Hello { SchemaVersion = ProtocolInfo.SchemaVersion + 1, ProcessKind = ProcessKind.Engine, Pid = 1 });
        Assert.True(ProtocolCodec.DecodeRequestFrame(other.Value).IsOk);
        Assert.True(ProtocolInfo.SchemaVersion >= 1);
    }

    [Fact]
    public void ProtocolResult_exposes_the_value_only_on_success()
    {
        var failed = ProtocolCodec.DecodeRequestFrame(Array.Empty<byte>());
        Assert.False(failed.IsOk);
        Assert.Throws<InvalidOperationException>(() => failed.Value);
        Assert.False(string.IsNullOrEmpty(failed.Failure.Detail));
    }

    [Fact]
    public void Decoded_strings_are_well_formed()
    {
        // Text from the wire is strict UTF-8: an overlong NUL (C0 80), a surrogate (ED A0 80) and a code point above U+10FFFF are refused.
        foreach (var bad in new byte[][] { [0xC0, 0x80], [0xED, 0xA0, 0x80], [0xF4, 0x90, 0x80, 0x80], [0xE2, 0x82], [0x80] })
        {
            var frame = RawFrame.Request((ref MessagePackWriter w) =>
            {
                w.WriteArrayHeader(2);
                w.Write(1UL);
                w.WriteStringHeader(bad.Length);
                w.WriteRaw(bad);
            }, MessageType.OpenDocument);
            Assert.Equal(Status.ProtocolError, ProtocolCodec.DecodeRequestFrame(frame).Status);
        }
    }
}
