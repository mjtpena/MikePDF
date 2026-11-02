using Windows.Win32;
using Windows.Win32.Storage.Xps;

namespace MikePDF.Printing;

/// <summary>A paper size the printer lists: the <c>DMPAPER_*</c> id, its name, and its size in tenths of a millimetre.</summary>
public sealed record PaperSizeInfo(int Id, string Name, int WidthTenthsMm, int HeightTenthsMm);

/// <summary>
/// What the spooler says a printer can do (<c>DeviceCapabilities</c>), which decides the options the print sheet shows (ADR-0011 decision 2). It is a first guess: the driver's merged
/// DEVMODE (<see cref="DevMode.Merge"/>) is the truth, and a driver may keep a copy count that <see cref="MaxCopies"/> says it cannot make (Microsoft Print to PDF does).
/// </summary>
public sealed record PrinterCapabilities(
    IReadOnlyList<PaperSizeInfo> PaperSizes,
    bool SupportsDuplex,
    bool SupportsColour,
    bool SupportsCollate,
    int MaxCopies,
    IReadOnlyList<(int X, int Y)> Resolutions,
    int LandscapeRotation)
{
    private const int MaxEntries = 4096;
    private const int NameChars = 64; // DC_PAPERNAMES entries are 64 characters

    /// <summary>Reads the capabilities. Never throws for a printer that answers -1 for something: that capability is then empty or false.</summary>
    public static unsafe PrinterCapabilities Read(string printer)
    {
        ArgumentException.ThrowIfNullOrEmpty(printer);

        int Count(PRINTER_DEVICE_CAPABILITIES capability)
        {
            fixed (char* name = printer)
            {
                return PInvoke.DeviceCapabilities(name, default, capability, default, null);
            }
        }

        // The count of entries first, then the entries into a buffer of exactly that size (capped: the driver is not ours).
        T[] Entries<T>(PRINTER_DEVICE_CAPABILITIES capability, int entryBytes)
            where T : unmanaged
        {
            var count = Math.Min(Count(capability), MaxEntries);
            if (count <= 0)
            {
                return [];
            }

            var raw = new byte[count * entryBytes];
            fixed (char* name = printer)
            fixed (byte* output = raw)
            {
                var got = PInvoke.DeviceCapabilities(name, default, capability, (Windows.Win32.Foundation.PWSTR)(char*)output, null);
                if (got <= 0)
                {
                    return [];
                }

                count = Math.Min(count, got);
            }

            var result = new T[count * entryBytes / sizeof(T)];
            fixed (byte* source = raw)
            fixed (T* target = result)
            {
                Buffer.MemoryCopy(source, target, (long)result.Length * sizeof(T), (long)count * entryBytes);
            }

            return result;
        }

        var ids = Entries<short>(PRINTER_DEVICE_CAPABILITIES.DC_PAPERS, sizeof(short));
        var names = Entries<char>(PRINTER_DEVICE_CAPABILITIES.DC_PAPERNAMES, NameChars * sizeof(char));
        var sizes = Entries<int>(PRINTER_DEVICE_CAPABILITIES.DC_PAPERSIZE, 2 * sizeof(int)); // POINT: x, y in tenths of a millimetre
        var papers = new List<PaperSizeInfo>(ids.Length);
        for (var i = 0; i < ids.Length; i++)
        {
            var name = (i + 1) * NameChars <= names.Length ? new string(names, i * NameChars, NameChars).TrimEnd('\0') : string.Empty;
            var (w, h) = (i + 1) * 2 <= sizes.Length ? (sizes[i * 2], sizes[(i * 2) + 1]) : (0, 0);
            papers.Add(new PaperSizeInfo(ids[i], name, w, h));
        }

        var resolutionValues = Entries<int>(PRINTER_DEVICE_CAPABILITIES.DC_ENUMRESOLUTIONS, 2 * sizeof(int));
        var resolutions = new List<(int, int)>(resolutionValues.Length / 2);
        for (var i = 0; i + 1 < resolutionValues.Length; i += 2)
        {
            resolutions.Add((resolutionValues[i], resolutionValues[i + 1]));
        }

        return new PrinterCapabilities(
            papers,
            Count(PRINTER_DEVICE_CAPABILITIES.DC_DUPLEX) == 1,
            Count(PRINTER_DEVICE_CAPABILITIES.DC_COLORDEVICE) == 1,
            Count(PRINTER_DEVICE_CAPABILITIES.DC_COLLATE) == 1,
            Math.Max(1, Count(PRINTER_DEVICE_CAPABILITIES.DC_COPIES)),
            resolutions,
            Math.Max(0, Count(PRINTER_DEVICE_CAPABILITIES.DC_ORIENTATION)));
    }
}
