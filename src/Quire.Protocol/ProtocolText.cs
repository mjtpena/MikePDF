using System.Buffers;
using System.Text;
using MessagePack;

namespace Quire.Protocol;

/// <summary>Strict UTF-8 handling shared by the formatters and Validate().</summary>
internal static class ProtocolText
{
    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>True for well-formed text of at most <paramref name="maxBytes"/> UTF-8 bytes with no NUL character.</summary>
    public static bool IsValid(string? value, int maxBytes)
    {
        if (value is null || value.Length > maxBytes || value.Contains('\0', StringComparison.Ordinal)) return false;
        try
        {
            return Strict.GetByteCount(value) <= maxBytes;
        }
        catch (EncoderFallbackException)
        {
            return false; // lone surrogate
        }
    }

    /// <summary>Decodes UTF-8 and rejects malformed sequences instead of substituting U+FFFD.</summary>
    public static string DecodeStrict(in ReadOnlySequence<byte> bytes)
    {
        try
        {
            return bytes.IsSingleSegment ? Strict.GetString(bytes.FirstSpan) : Strict.GetString(bytes.ToArray());
        }
        catch (DecoderFallbackException e)
        {
            throw new MessagePackSerializationException("String is not valid UTF-8.", e);
        }
    }
}
