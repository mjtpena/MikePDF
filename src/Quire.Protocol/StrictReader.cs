using MessagePack;

namespace Quire.Protocol;

/// <summary>A well-formed value that breaks a schema limit. The codec reports it as InvalidArgument rather than ProtocolError.</summary>
internal sealed class ProtocolLimitException(string message) : Exception(message);

/// <summary>
/// The decoding primitives the generated Read methods are built from. It mirrors the C++ Reader one for one, so both sides
/// accept the same bytes and fail in the same order with the same status:
/// a value of the wrong shape or type, or one that does not fit its wire type, throws a MessagePack or overflow exception
/// (ProtocolError); a value of the right type outside its schema limit throws ProtocolLimitException (InvalidArgument).
/// Every count and length is checked against the bytes remaining and then against the schema limit before anything is
/// allocated. Nothing here recurses.
/// </summary>
internal static class StrictReader
{
    public static ProtocolLimitException Limit(string what) => new(what);

    /// <summary>An array header whose count must be exactly <paramref name="exact"/>.</summary>
    public static void ExpectArray(ref MessagePackReader reader, int exact)
    {
        // ReadArrayHeader throws if the count claims more elements than there are bytes left.
        if (reader.ReadArrayHeader() != exact) throw new MessagePackSerializationException("Wrong number of fields.");
    }

    public static int ReadArrayHeader(ref MessagePackReader reader, int maxCount)
    {
        var count = reader.ReadArrayHeader();
        return count > maxCount ? throw Limit("Array has more elements than its limit.") : count;
    }

    public static bool ReadBool(ref MessagePackReader reader) => reader.ReadBoolean();

    public static ushort ReadU16(ref MessagePackReader reader, ushort min, ushort max)
    {
        var v = reader.ReadUInt16();
        return v < min || v > max ? throw Limit("Integer is outside its range.") : v;
    }

    public static uint ReadU32(ref MessagePackReader reader, uint min, uint max)
    {
        var v = reader.ReadUInt32();
        return v < min || v > max ? throw Limit("Integer is outside its range.") : v;
    }

    public static ulong ReadU64(ref MessagePackReader reader, ulong min, ulong max)
    {
        var v = reader.ReadUInt64();
        return v < min || v > max ? throw Limit("Integer is outside its range.") : v;
    }

    public static int ReadI32(ref MessagePackReader reader, int min, int max)
    {
        var v = reader.ReadInt32();
        return v < min || v > max ? throw Limit("Integer is outside its range.") : v;
    }

    /// <summary>float32 only: the stock reader also takes float64 and integers, which the C++ reader refuses.</summary>
    public static float ReadF32(ref MessagePackReader reader, float min, float max, bool minExclusive)
    {
        if (reader.NextCode != MessagePackCode.Float32) throw new MessagePackSerializationException("Expected float32.");
        var v = reader.ReadSingle();
        if (!float.IsFinite(v)) throw Limit("Float is not finite.");
        return v > max || (minExclusive ? v <= min : v < min) ? throw Limit("Float is outside its range.") : v;
    }

    public static string ReadString(ref MessagePackReader reader, int maxBytes) =>
        ReadText(ref reader, maxBytes) ?? throw new MessagePackSerializationException("Expected string, found nil.");

    public static string? ReadOptionalString(ref MessagePackReader reader, int maxBytes) => ReadText(ref reader, maxBytes);

    // Nil comes back as null, for the caller to accept or refuse.
    private static string? ReadText(ref MessagePackReader reader, int maxBytes)
    {
        if (reader.TryReadNil()) return null;

        // Throws if the length claims more bytes than remain, before anything is allocated.
        var bytes = reader.ReadStringSequence() ?? throw new MessagePackSerializationException("Expected string.");
        if (bytes.Length > maxBytes) throw Limit("String is longer than its limit.");
        var text = ProtocolText.DecodeStrict(bytes);
        return text.Contains('\0', StringComparison.Ordinal) ? throw Limit("String contains NUL.") : text;
    }
}
