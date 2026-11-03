namespace MikePDF.Core.Editing;

/// <summary>CRC-32C (Castagnoli, reflected polynomial 0x82F63B78): the checksum of every journal record (save-pipeline 3.4).</summary>
public static class Crc32C
{
    private static readonly uint[] Table = BuildTable();

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? (value >> 1) ^ 0x82F63B78u : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }
}
