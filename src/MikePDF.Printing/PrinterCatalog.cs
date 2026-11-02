using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Graphics.Printing;

namespace MikePDF.Printing;

/// <summary>A printer queue the person can print to.</summary>
public sealed record PrinterInfo(string Name, bool IsDefault, bool IsNetwork);

/// <summary>
/// The printers and the default, read from the spooler (<c>EnumPrinters</c>, <c>GetDefaultPrinter</c>). Read-only: nothing here sets the default printer or changes a queue,
/// and nothing polls a printer's state (print.md 6).
/// </summary>
public static class PrinterCatalog
{
    private const uint EnumLocal = 0x2;
    private const uint EnumConnections = 0x4;
    private const uint AttributeNetwork = 0x10;
    private const int MaxPrinters = 4096;

    /// <summary>Every local printer and every connection to a shared one, the default first and the rest by name.</summary>
    public static unsafe IReadOnlyList<PrinterInfo> Enumerate()
    {
        const uint level = 4; // PRINTER_INFO_4: name, server, attributes; it does not ask the printer anything
        PInvoke.EnumPrinters(EnumLocal | EnumConnections, null, level, null, 0, out var needed, out _);
        if (needed == 0 || needed > 16 << 20)
        {
            return [];
        }

        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!PInvoke.EnumPrinters(EnumLocal | EnumConnections, null, level, (byte*)buffer, needed, out _, out var returned))
            {
                return [];
            }

            var defaultName = GetDefaultName();
            var list = new List<PrinterInfo>();
            var items = (PRINTER_INFO_4W*)buffer;
            for (var i = 0; i < Math.Min(returned, MaxPrinters); i++)
            {
                var name = items[i].pPrinterName.ToString();
                if (!string.IsNullOrEmpty(name))
                {
                    list.Add(new PrinterInfo(name, string.Equals(name, defaultName, StringComparison.OrdinalIgnoreCase), (items[i].Attributes & AttributeNetwork) != 0));
                }
            }

            return [.. list.OrderByDescending(p => p.IsDefault).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)];
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>The name of the person's default printer, or null when there is none.</summary>
    public static unsafe string? GetDefaultName()
    {
        uint length = 0;
        PInvoke.GetDefaultPrinter(default, &length);
        if (length == 0 || length > 4096)
        {
            return null;
        }

        var buffer = new char[length];
        fixed (char* p = buffer)
        {
            if (!PInvoke.GetDefaultPrinter(p, &length))
            {
                return null;
            }
        }

        return new string(buffer, 0, (int)Math.Max(0, length - 1));
    }

    /// <summary>Whether a printer of this name exists (case-insensitive), for a test that skips when a queue is missing.</summary>
    public static bool Exists(string name) => Enumerate().Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
}
