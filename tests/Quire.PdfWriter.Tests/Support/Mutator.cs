using System.Text;

namespace Quire.PdfWriter.Tests.Support;

/// <summary>Deterministic byte-level and token-level mutations of PDF files, for the randomised robustness and differential tests.</summary>
internal static class Mutator
{
    private static readonly byte[] Interesting = [0, 1, 0x7F, 0xFF, (byte)'<', (byte)'>', (byte)'(', (byte)')', (byte)'[', (byte)']', (byte)'/', (byte)'%', (byte)' ', 10, 13, (byte)'0', (byte)'9', (byte)'-', (byte)'.', (byte)'R', (byte)'#', (byte)'\\'];

    private static readonly string[] Snippets =
    [
        "endobj", "stream\n", "\nendstream", "xref\n", "trailer", "startxref\n0\n%%EOF\n", " 0 R", "/Prev 0", "/Prev 9", "<<", ">>", "/Length 3", "/Type /XRef",
        "/Size 3", "/W [4 4 4]", "/Index [0 9999999]", "/Root 1 0 R", "/Encrypt 1 0 R", "/XRefStm 9", "/Filter /FlateDecode", "/N 100000", "/First 5", "obj", "1 0 obj\n",
    ];

    private static readonly string[] Numbers = ["0", "1", "2", "7", "8", "9", "65535", "65536", "2147483647", "2147483648", "-1", "99999999999", "4194303", "4194304", "000"];

    public static byte[] Mutate(Random rng, byte[] seed)
    {
        var data = new List<byte>(seed);
        int rounds = 1 + rng.Next(3);
        for (int r = 0; r < rounds && data.Count > 0; r++)
        {
            // Layout-preserving edits (flip, overwrite, number swap) are weighted up; offsets-shifting ones still occur often.
            int op = rng.Next(14) switch { <= 2 => 0, <= 5 => 1, <= 8 => 7, 9 => 2, 10 => 3, 11 => 4, 12 => 5, _ => rng.Next(2) == 0 ? 6 : 8 };
            switch (op)
            {
                case 0: // flip bits
                    for (int k = 1 + rng.Next(6); k > 0; k--)
                        data[rng.Next(data.Count)] ^= (byte)(1 << rng.Next(8));
                    break;
                case 1: // overwrite with interesting bytes
                    for (int k = 1 + rng.Next(4); k > 0; k--)
                        data[rng.Next(data.Count)] = Interesting[rng.Next(Interesting.Length)];
                    break;
                case 2: // delete a chunk
                {
                    int at = rng.Next(data.Count);
                    data.RemoveRange(at, Math.Min(data.Count - at, 1 + rng.Next(48)));
                    break;
                }

                case 3: // insert random bytes
                {
                    var chunk = new byte[1 + rng.Next(24)];
                    rng.NextBytes(chunk);
                    data.InsertRange(rng.Next(data.Count + 1), chunk);
                    break;
                }

                case 4: // insert a structural snippet
                    data.InsertRange(rng.Next(data.Count + 1), Encoding.Latin1.GetBytes(Snippets[rng.Next(Snippets.Length)]));
                    break;
                case 5: // duplicate a chunk elsewhere
                {
                    int at = rng.Next(data.Count);
                    var chunk = data.GetRange(at, Math.Min(data.Count - at, 1 + rng.Next(64)));
                    data.InsertRange(rng.Next(data.Count + 1), chunk);
                    break;
                }

                case 6: // truncate the tail
                {
                    int cut = Math.Min(data.Count, 1 + rng.Next(80));
                    data.RemoveRange(data.Count - cut, cut);
                    break;
                }
                case 7: // replace a run of digits with another number
                    ReplaceNumber(rng, data);
                    break;
                default: // swap two chunks
                {
                    int a = rng.Next(data.Count);
                    int b = rng.Next(data.Count);
                    int len = Math.Min(Math.Min(data.Count - a, data.Count - b), 1 + rng.Next(16));
                    for (int i = 0; i < len; i++)
                        (data[a + i], data[b + i]) = (data[b + i], data[a + i]);
                    break;
                }
            }
        }

        return [.. data];
    }

    private static void ReplaceNumber(Random rng, List<byte> data)
    {
        // Find a digit run starting at a random position (scanning forward so the choice is uniform enough over the file).
        int start = rng.Next(data.Count);
        int i = start;
        while (i < data.Count && !IsDigit(data[i]))
            i++;
        if (i >= data.Count)
            return;
        int j = i;
        while (j < data.Count && IsDigit(data[j]))
            j++;
        data.RemoveRange(i, j - i);
        data.InsertRange(i, Encoding.Latin1.GetBytes(Numbers[rng.Next(Numbers.Length)]));
    }

    private static bool IsDigit(byte b) => b is >= (byte)'0' and <= (byte)'9';
}
