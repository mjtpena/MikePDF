using System.Buffers;
using System.Buffers.Binary;
using MessagePack;

namespace MikePDF.Protocol.Tests;

public delegate void WriteBody(ref MessagePackWriter writer);

/// <summary>Builds frames byte by byte, so tests can produce things the real encoder refuses to.</summary>
public static class RawFrame
{
    public static byte[] Bytes(WriteBody write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        write(ref writer);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    public static byte[] Prefix(byte[] body) => Prefix(body, (uint)body.Length);

    public static byte[] Prefix(byte[] body, uint declaredLength)
    {
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, declaredLength);
        body.CopyTo(frame, 4);
        return frame;
    }

    /// <summary>A request frame with a hand-written payload.</summary>
    public static byte[] Request(WriteBody payload, MessageType type, ulong requestId = 1, uint documentId = 0, uint deadlineMs = 0) =>
        Prefix(Bytes((ref MessagePackWriter w) =>
        {
            w.WriteArrayHeader(5);
            w.Write(requestId);
            w.Write(documentId);
            w.Write(deadlineMs);
            w.Write((ushort)type);
            payload(ref w);
        }));

    /// <summary>A response frame with a hand-written payload (or nil).</summary>
    public static byte[] Response(WriteBody? payload, MessageType type, Status status = Status.Ok, ulong requestId = 1) =>
        Prefix(Bytes((ref MessagePackWriter w) =>
        {
            w.WriteArrayHeader(4);
            w.Write(requestId);
            w.Write((ushort)status);
            w.Write((ushort)type);
            if (payload is null) w.WriteNil();
            else payload(ref w);
        }));

    /// <summary>Writes a payload object exactly as the generated Write does, bypassing Validate().</summary>
    public static WriteBody Serialized(object payload) => (ref MessagePackWriter w) =>
    {
        ((IProtocolMessage)payload).Write(ref w);
    };

    public static byte[] RequestOf(IRequestMessage payload, ulong requestId = 1, uint documentId = 0, uint deadlineMs = 0) =>
        Request(Serialized(payload), payload.MessageType, requestId, documentId, deadlineMs);

    public static byte[] ResponseOf(IReplyMessage payload, Status status = Status.Ok, ulong requestId = 1) =>
        Response(Serialized(payload), payload.MessageType, status, requestId);

    /// <summary>The document id a valid request of this type must carry.</summary>
    public static uint DocumentFor(MessageType type) => MessageTypeInfo.DocumentRule(type) == DocumentIdRule.Required ? 1u : 0u;
}
