using System.ComponentModel;
using System.Runtime.InteropServices;
using MikePDF.Core.Printing;
using Windows.Win32;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.Graphics.Printing;

namespace MikePDF.Printing;

/// <summary>The driver or the spooler refused something. <see cref="Win32Error"/> is the <c>GetLastError</c> value, or 0.</summary>
public sealed class PrintDeviceException : Exception
{
    public PrintDeviceException(string message, int win32Error = 0)
        : base(win32Error == 0 ? message : $"{message}: {new Win32Exception(win32Error).Message} (error {win32Error})")
    {
        Win32Error = win32Error;
    }

    public int Win32Error { get; }

    internal static PrintDeviceException FromLastError(string function) => new(function + " failed", Marshal.GetLastWin32Error());
}

/// <summary>
/// A printer's <c>DEVMODEW</c> as bytes: its settings plus the driver's private part. The public part is edited at the offsets of the structure (they are the same on x64 and
/// ARM64), the whole is handed to the driver untouched, because the private part belongs to it. What the driver does to a request is read back after
/// <see cref="Merge"/>: <b>the driver's merged DEVMODE is the truth</b> (ADR-0011 decision 2), so a setting it clears is "not applied", not an error.
/// </summary>
public sealed class DevMode
{
    // Offsets in DEVMODEW (wingdi.h; spec version 0x0401 and later).
    private const int OffsetSize = 68;
    private const int OffsetDriverExtra = 70;
    private const int OffsetFields = 72;
    private const int OffsetOrientation = 76;
    private const int OffsetPaperSize = 78;
    private const int OffsetCopies = 86;
    private const int OffsetColor = 92;
    private const int OffsetDuplex = 94;
    private const int OffsetCollate = 100;
    private const int MinimumSize = 156; // up to dmDisplayFrequency, the part every driver has

    public const int FieldOrientation = 0x1;
    public const int FieldPaperSize = 0x2;
    public const int FieldCopies = 0x100;
    public const int FieldColor = 0x800;
    public const int FieldDuplex = 0x1000;
    public const int FieldCollate = 0x8000;

    public const short Portrait = 1;
    public const short Landscape = 2;
    public const short Monochrome = 1;
    public const short Colour = 2;
    public const short DuplexNone = 1;
    public const short DuplexLongEdge = 2; // DMDUP_VERTICAL
    public const short DuplexShortEdge = 3; // DMDUP_HORIZONTAL

    private readonly byte[] _bytes;

    public DevMode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length < MinimumSize || BitConverter.ToUInt16(bytes, OffsetSize) + (uint)BitConverter.ToUInt16(bytes, OffsetDriverExtra) > bytes.Length)
        {
            throw new ArgumentException("Not a DEVMODE.", nameof(bytes));
        }

        _bytes = bytes;
    }

    public ReadOnlySpan<byte> Bytes => _bytes;

    public int Fields => BitConverter.ToInt32(_bytes, OffsetFields);

    public short Orientation => Read(OffsetOrientation, FieldOrientation);

    public short PaperSize => Read(OffsetPaperSize, FieldPaperSize);

    public short Copies => Read(OffsetCopies, FieldCopies);

    public short ColorMode => Read(OffsetColor, FieldColor);

    public short Duplex => Read(OffsetDuplex, FieldDuplex);

    public short Collate => Read(OffsetCollate, FieldCollate);

    public bool Has(int field) => (Fields & field) != 0;

    public DevMode Clone() => new((byte[])_bytes.Clone());

    public void SetOrientation(bool landscape) => Write(OffsetOrientation, FieldOrientation, landscape ? Landscape : Portrait);

    public void SetPaperSize(int paperSizeId) => Write(OffsetPaperSize, FieldPaperSize, (short)paperSizeId);

    public void SetCopies(int copies) => Write(OffsetCopies, FieldCopies, (short)Math.Clamp(copies, 1, 99));

    public void SetCollate(bool collate) => Write(OffsetCollate, FieldCollate, (short)(collate ? 1 : 0));

    public void SetColour(bool colour) => Write(OffsetColor, FieldColor, colour ? Colour : Monochrome);

    public void SetDuplex(PrintDuplex duplex) => Write(OffsetDuplex, FieldDuplex, duplex switch
    {
        PrintDuplex.LongEdge => DuplexLongEdge,
        PrintDuplex.ShortEdge => DuplexShortEdge,
        _ => DuplexNone,
    });

    public override string ToString() =>
        $"fields=0x{Fields:X} orientation={Orientation} paper={PaperSize} copies={Copies} collate={Collate} colour={ColorMode} duplex={Duplex} size={_bytes.Length}";

    /// <summary>The driver's default DEVMODE for the printer (no dialog). Throws <see cref="PrintDeviceException"/>.</summary>
    public static unsafe DevMode GetDefault(string printer) => WithPrinter(printer, handle => Call(handle, printer, null, DocumentPropertiesMode.Out));

    /// <summary>
    /// Asks the driver to merge <paramref name="requested"/> with its own defaults and validate it (<c>DM_IN_BUFFER | DM_OUT_BUFFER</c>, no dialog). What the result still says is
    /// what the driver accepted.
    /// </summary>
    public static DevMode Merge(string printer, DevMode requested)
    {
        ArgumentNullException.ThrowIfNull(requested);
        return WithPrinter(printer, handle => Call(handle, printer, requested, DocumentPropertiesMode.In | DocumentPropertiesMode.Out));
    }

    /// <summary>
    /// The driver's own preferences dialog (<c>DocumentProperties</c> with <c>DM_PROMPT</c>), the print sheet's "Printer preferences..." link. Shows UI and blocks until it is closed, so call it
    /// from a UI thread with the sheet's window. Returns the edited DEVMODE, or null when the person cancelled. Nothing is saved as the printer's defaults.
    /// </summary>
    public static DevMode? ShowPreferences(nint ownerWindow, string printer, DevMode current)
    {
        ArgumentNullException.ThrowIfNull(current);
        try
        {
            return WithPrinter(printer, handle => Call(handle, printer, current, DocumentPropertiesMode.In | DocumentPropertiesMode.Out | DocumentPropertiesMode.Prompt, ownerWindow));
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    [Flags]
    private enum DocumentPropertiesMode : uint
    {
        Out = 2,
        Prompt = 4,
        In = 8,
    }

    private static T WithPrinter<T>(string printer, Func<PRINTER_HANDLE, T> action)
    {
        ArgumentException.ThrowIfNullOrEmpty(printer);
        if (!PInvoke.OpenPrinter(printer, out ClosePrinterSafeHandle handle, null))
        {
            throw PrintDeviceException.FromLastError("OpenPrinter");
        }

        using (handle)
        {
            return action(new PRINTER_HANDLE(handle.DangerousGetHandle()));
        }
    }

    private static unsafe DevMode Call(PRINTER_HANDLE handle, string printer, DevMode? input, DocumentPropertiesMode mode, nint owner = 0)
    {
        fixed (char* name = printer)
        {
            var hwnd = new Windows.Win32.Foundation.HWND(owner);
            var size = PInvoke.DocumentProperties(hwnd, handle, name, null, null, 0);
            if (size <= 0)
            {
                throw PrintDeviceException.FromLastError("DocumentProperties (size)");
            }

            var output = new byte[size];
            fixed (byte* pOut = output)
            {
                int result;
                if (input is null)
                {
                    result = PInvoke.DocumentProperties(hwnd, handle, name, (DEVMODEW*)pOut, null, (uint)mode);
                }
                else
                {
                    // The input buffer is copied to the size the driver wants: a DEVMODE from an older driver version may be shorter.
                    var copy = new byte[Math.Max(size, input._bytes.Length)];
                    input._bytes.CopyTo(copy, 0);
                    fixed (byte* pIn = copy)
                    {
                        result = PInvoke.DocumentProperties(hwnd, handle, name, (DEVMODEW*)pOut, (DEVMODEW*)pIn, (uint)mode);
                    }
                }

                const int IdCancel = 2;
                if (result == IdCancel && (mode & DocumentPropertiesMode.Prompt) != 0)
                {
                    throw new OperationCanceledException();
                }

                if (result < 0)
                {
                    throw PrintDeviceException.FromLastError("DocumentProperties");
                }
            }

            return new DevMode(output);
        }
    }

    private short Read(int offset, int field) => Has(field) ? BitConverter.ToInt16(_bytes, offset) : (short)0;

    private void Write(int offset, int field, short value)
    {
        BitConverter.TryWriteBytes(_bytes.AsSpan(OffsetFields), Fields | field);
        BitConverter.TryWriteBytes(_bytes.AsSpan(offset), value);
    }
}
