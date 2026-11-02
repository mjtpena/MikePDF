using System.Buffers;
using System.Buffers.Binary;
using MessagePack;

namespace MikePDF.Protocol;

/// <summary>
/// Frames, envelopes and payloads. Wire layout (schema/mikepdf.protocol.json):
/// <code>
/// frame    = u32le bodyLength, body            (0 &lt; bodyLength &lt;= MaxFrameBytes)
/// request  = [requestId u64, documentId u32, deadlineMs u32, messageType u16, payload]
/// response = [requestId u64, status u16, messageType u16, payload | nil]
/// payload  = array of exactly the fields of the message, in key order
/// </code>
/// Every decoder here treats its input as hostile: nothing is returned that has not passed the schema limits, a bad
/// input yields a failure and never an exception, and decoding is bounded by the frame size.
/// </summary>
public static class ProtocolCodec
{
    public const int FrameHeaderBytes = 4;

    // ---- framing -------------------------------------------------------------------------------

    /// <summary>
    /// Reads the length prefix. Call this on the first four bytes of a stream before reading the body, so an oversize
    /// length is refused without buffering anything.
    /// </summary>
    public static ProtocolFailure? ReadFrameLength(ReadOnlySpan<byte> header, out int bodyLength)
    {
        bodyLength = 0;
        if (header.Length < FrameHeaderBytes) return ProtocolFailure.Malformed("Truncated frame header.");
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length == 0) return ProtocolFailure.Malformed("Empty frame.");
        if (length > ProtocolInfo.MaxFrameBytes) return ProtocolFailure.Malformed("Frame exceeds the maximum size.");
        bodyLength = (int)length;
        return null;
    }

    private static ProtocolFailure? SplitFrame(ReadOnlyMemory<byte> frame, out ReadOnlyMemory<byte> body)
    {
        body = default;
        if (ReadFrameLength(frame.Span, out var length) is { } failure) return failure;
        var available = frame.Length - FrameHeaderBytes;
        if (available < length) return ProtocolFailure.Malformed("Truncated frame.");
        if (available > length) return ProtocolFailure.Malformed("Bytes after the end of the frame.");
        body = frame.Slice(FrameHeaderBytes, length);
        return null;
    }

    // ---- decoding ------------------------------------------------------------------------------

    public static ProtocolResult<RequestEnvelope> DecodeRequestFrame(ReadOnlyMemory<byte> frame) =>
        SplitFrame(frame, out var body) is { } failure ? ProtocolResult.Fail<RequestEnvelope>(failure) : DecodeRequestBody(body);

    public static ProtocolResult<ResponseEnvelope> DecodeResponseFrame(ReadOnlyMemory<byte> frame) =>
        SplitFrame(frame, out var body) is { } failure ? ProtocolResult.Fail<ResponseEnvelope>(failure) : DecodeResponseBody(body);

    public static ProtocolResult<RequestEnvelope> DecodeRequestBody(ReadOnlyMemory<byte> body)
    {
        try
        {
            var reader = new MessagePackReader(body);
            if (reader.ReadArrayHeader() != 5) return Fail<RequestEnvelope>(ProtocolFailure.Malformed("A request header has five elements."));
            var requestId = reader.ReadUInt64();
            var documentId = reader.ReadUInt32();
            var deadlineMs = reader.ReadUInt32();
            var type = (MessageType)reader.ReadUInt16();
            if (!MessageTypeInfo.IsDefined(type)) return Fail<RequestEnvelope>(ProtocolFailure.Malformed("Unknown message type."));
            if (CheckRequestHeader(documentId, deadlineMs, type) is { } header) return Fail<RequestEnvelope>(header);

            var payload = ProtocolDispatch.ReadRequest(type, ref reader);
            if (!reader.End) return Fail<RequestEnvelope>(ProtocolFailure.Malformed("Bytes after the end of the message."));
            if (payload.Validate() is { } invalid) return Fail<RequestEnvelope>(invalid);
            return ProtocolResult.Ok(new RequestEnvelope(requestId, documentId, deadlineMs, type, payload));
        }
        catch (Exception e) when (IsDecodeFailure(e))
        {
            return Fail<RequestEnvelope>(Classify(e));
        }
    }

    public static ProtocolResult<ResponseEnvelope> DecodeResponseBody(ReadOnlyMemory<byte> body)
    {
        try
        {
            var reader = new MessagePackReader(body);
            if (reader.ReadArrayHeader() != 4) return Fail<ResponseEnvelope>(ProtocolFailure.Malformed("A response header has four elements."));
            var requestId = reader.ReadUInt64();
            var status = (Status)reader.ReadUInt16();
            var type = (MessageType)reader.ReadUInt16();
            if (!StatusInfo.IsDefined(status)) return Fail<ResponseEnvelope>(ProtocolFailure.Malformed("Unknown status."));
            if (!MessageTypeInfo.IsDefined(type)) return Fail<ResponseEnvelope>(ProtocolFailure.Malformed("Unknown message type."));

            IReplyMessage? payload = null;
            if (StatusInfo.CarriesPayload(status))
            {
                payload = ProtocolDispatch.ReadReply(type, ref reader);
            }
            else if (!reader.TryReadNil())
            {
                return Fail<ResponseEnvelope>(ProtocolFailure.Malformed("An error response carries no payload."));
            }

            if (!reader.End) return Fail<ResponseEnvelope>(ProtocolFailure.Malformed("Bytes after the end of the message."));
            if (payload?.Validate() is { } invalid) return Fail<ResponseEnvelope>(invalid);
            return ProtocolResult.Ok(new ResponseEnvelope(requestId, status, type, payload));
        }
        catch (Exception e) when (IsDecodeFailure(e))
        {
            return Fail<ResponseEnvelope>(Classify(e));
        }
    }

    private static ProtocolFailure? CheckRequestHeader(uint documentId, uint deadlineMs, MessageType type)
    {
        if (deadlineMs > ProtocolInfo.MaxDeadlineMs) return ProtocolFailure.Invalid("Deadline is out of range.");
        return MessageTypeInfo.DocumentRule(type) switch
        {
            DocumentIdRule.None when documentId != 0 => ProtocolFailure.Invalid("This message takes no document."),
            DocumentIdRule.Required when documentId == 0 => ProtocolFailure.Invalid("This message needs a document."),
            _ => null,
        };
    }

    private static ProtocolResult<T> Fail<T>(ProtocolFailure failure) where T : class => ProtocolResult.Fail<T>(failure);

    private static bool IsDecodeFailure(Exception e) =>
        e is ProtocolLimitException or MessagePackSerializationException or EndOfStreamException or OverflowException or InsufficientExecutionStackException
            or InvalidOperationException or ArgumentException;

    // A limit breach is InvalidArgument; every other decoding exception means the bytes are not a valid message.
    private static ProtocolFailure Classify(Exception e)
    {
        for (var inner = e; inner is not null; inner = inner.InnerException)
            if (inner is ProtocolLimitException) return ProtocolFailure.Invalid(inner.Message);
        return ProtocolFailure.Malformed(e.Message);
    }

    // ---- encoding ------------------------------------------------------------------------------

    /// <summary>Validates the message and returns a complete frame, or the reason it cannot be sent.</summary>
    public static ProtocolResult<byte[]> EncodeRequestFrame(ulong requestId, uint documentId, uint deadlineMs, IRequestMessage payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var type = payload.MessageType;
        if (!MessageTypeInfo.IsDefined(type)) return Fail<byte[]>(ProtocolFailure.Invalid("Unknown message type."));
        if (payload.Validate() is { } invalid) return Fail<byte[]>(invalid);
        if (CheckRequestHeader(documentId, deadlineMs, type) is { } header) return Fail<byte[]>(header);

        var buffer = StartFrame();
        var writer = new MessagePackWriter(buffer);
        writer.WriteArrayHeader(5);
        writer.Write(requestId);
        writer.Write(documentId);
        writer.Write(deadlineMs);
        writer.Write((ushort)type);
        ProtocolDispatch.WriteRequest(payload, ref writer);
        writer.Flush();
        return FinishFrame(buffer);
    }

    /// <summary>
    /// Encodes a response. <paramref name="payload"/> must be present exactly when <paramref name="status"/> carries one,
    /// and must belong to <paramref name="type"/>.
    /// </summary>
    public static ProtocolResult<byte[]> EncodeResponseFrame(ulong requestId, Status status, MessageType type, IReplyMessage? payload)
    {
        if (!StatusInfo.IsDefined(status)) return Fail<byte[]>(ProtocolFailure.Invalid("Unknown status."));
        if (!MessageTypeInfo.IsDefined(type)) return Fail<byte[]>(ProtocolFailure.Invalid("Unknown message type."));
        if (StatusInfo.CarriesPayload(status) != (payload is not null)) return Fail<byte[]>(ProtocolFailure.Invalid("The payload does not match the status."));
        if (payload is not null)
        {
            if (payload.MessageType != type) return Fail<byte[]>(ProtocolFailure.Invalid("The payload belongs to another message type."));
            if (payload.Validate() is { } invalid) return Fail<byte[]>(invalid);
        }

        var buffer = StartFrame();
        var writer = new MessagePackWriter(buffer);
        writer.WriteArrayHeader(4);
        writer.Write(requestId);
        writer.Write((ushort)status);
        writer.Write((ushort)type);
        if (payload is null) writer.WriteNil();
        else ProtocolDispatch.WriteReply(payload, ref writer);
        writer.Flush();
        return FinishFrame(buffer);
    }

    private static ArrayBufferWriter<byte> StartFrame()
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        buffer.GetSpan(FrameHeaderBytes)[..FrameHeaderBytes].Clear();
        buffer.Advance(FrameHeaderBytes);
        return buffer;
    }

    private static ProtocolResult<byte[]> FinishFrame(ArrayBufferWriter<byte> buffer)
    {
        var frame = buffer.WrittenMemory.ToArray();
        var bodyLength = frame.Length - FrameHeaderBytes;
        if (bodyLength > ProtocolInfo.MaxFrameBytes) return Fail<byte[]>(ProtocolFailure.Invalid("The message does not fit in one frame."));
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)bodyLength);
        return ProtocolResult.Ok(frame);
    }
}
