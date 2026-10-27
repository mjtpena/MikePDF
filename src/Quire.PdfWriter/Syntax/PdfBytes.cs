namespace Quire.PdfWriter;

/// <summary>Character classes of ISO 32000-2 clause 7.2.3.</summary>
internal static class PdfBytes
{
    public static bool IsWhitespace(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;

    public static bool IsEol(byte b) => b is 10 or 13;

    public static bool IsDelimiter(byte b) =>
        b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    public static bool IsRegular(byte b) => !IsWhitespace(b) && !IsDelimiter(b);

    public static bool IsDigit(byte b) => b is >= (byte)'0' and <= (byte)'9';

    public static int HexValue(byte c) => c switch
    {
        >= (byte)'0' and <= (byte)'9' => c - '0',
        >= (byte)'a' and <= (byte)'f' => c - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => c - 'A' + 10,
        _ => -1,
    };

    /// <summary>True when <paramref name="keyword"/> occurs at <paramref name="pos"/> and is not followed by a regular character.</summary>
    public static bool KeywordAt(ReadOnlySpan<byte> data, int pos, ReadOnlySpan<byte> keyword)
    {
        if (pos < 0 || pos > data.Length - keyword.Length)
            return false;
        if (!data.Slice(pos, keyword.Length).SequenceEqual(keyword))
            return false;
        int end = pos + keyword.Length;
        return end >= data.Length || !IsRegular(data[end]);
    }

    public static int SkipWhitespace(ReadOnlySpan<byte> data, int pos)
    {
        while (pos < data.Length && IsWhitespace(data[pos]))
            pos++;
        return pos;
    }

    /// <summary>Skips whitespace and %-comments.</summary>
    public static int SkipWhitespaceAndComments(ReadOnlySpan<byte> data, int pos)
    {
        while (pos < data.Length)
        {
            byte b = data[pos];
            if (IsWhitespace(b))
            {
                pos++;
            }
            else if (b == (byte)'%')
            {
                while (pos < data.Length && !IsEol(data[pos]))
                    pos++;
            }
            else
            {
                break;
            }
        }

        return pos;
    }
}
