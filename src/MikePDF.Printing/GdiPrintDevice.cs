using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MikePDF.Core.Printing;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.Storage.Xps;

namespace MikePDF.Printing;

/// <summary>
/// A printer device context driven with <c>StartDoc</c>, <c>StartPage</c>, <c>StretchDIBits</c>, <c>EndPage</c> and <c>EndDoc</c> (ADR-0011 decision 3). One device context and one <c>StartDoc</c>
/// per job. Cancelling is two things: an abort procedure the spooler calls during a long call returns FALSE once <see cref="RequestAbort"/> was called, and <see cref="AbortDocument"/>
/// ends the job. The engine never sees the device context (ADR-0011 decision 7).
/// </summary>
public sealed class GdiPrintDevice : IPrintDevice
{
    private static readonly ConcurrentDictionary<nint, GdiPrintDevice> Devices = new();

    private readonly HDC _dc;
    private readonly nint _key;
    private int _abortRequested;
    private bool _documentStarted;
    private bool _documentEnded;
    private bool _disposed;

    private GdiPrintDevice(HDC dc, nint key, PaperArea paper)
    {
        _dc = dc;
        _key = key;
        Paper = paper;
    }

    public PaperArea Paper { get; }

    /// <summary>
    /// Creates the device context for <paramref name="printer"/> with a validated DEVMODE and reads the printable area. Throws <see cref="PrintDeviceException"/>.
    /// </summary>
    public static unsafe GdiPrintDevice Create(string printer, DevMode devMode)
    {
        ArgumentException.ThrowIfNullOrEmpty(printer);
        ArgumentNullException.ThrowIfNull(devMode);
        HDC dc;
        fixed (char* driver = "WINSPOOL")
        fixed (char* device = printer)
        fixed (byte* mode = devMode.Bytes)
        {
            dc = PInvoke.CreateDCW(driver, device, default, (DEVMODEW*)mode);
        }

        if (dc.IsNull)
        {
            throw PrintDeviceException.FromLastError("CreateDC");
        }

        var paper = new PaperArea(
            PInvoke.GetDeviceCaps(dc, GET_DEVICE_CAPS_INDEX.HORZRES),
            PInvoke.GetDeviceCaps(dc, GET_DEVICE_CAPS_INDEX.VERTRES),
            PInvoke.GetDeviceCaps(dc, GET_DEVICE_CAPS_INDEX.LOGPIXELSX),
            PInvoke.GetDeviceCaps(dc, GET_DEVICE_CAPS_INDEX.LOGPIXELSY));
        if (paper.WidthPx < 1 || paper.HeightPx < 1 || paper.DpiX < 1 || paper.DpiY < 1)
        {
            PInvoke.DeleteDC(dc);
            throw new PrintDeviceException("The printer reported no printable area");
        }

        var device1 = new GdiPrintDevice(dc, (nint)dc.Value, paper);
        Devices[(nint)dc.Value] = device1;
        _ = PInvoke.SetAbortProc(dc, &AbortProcedure);
        return device1;
    }

    public unsafe void StartDocument(string documentName, string? outputPath)
    {
        ArgumentNullException.ThrowIfNull(documentName);
        fixed (char* name = documentName)
        fixed (char* output = outputPath)
        {
            var info = new DOCINFOW
            {
                cbSize = sizeof(DOCINFOW),
                lpszDocName = name,
                lpszOutput = outputPath is null ? default : output,
                lpszDatatype = default,
            };
            if (PInvoke.StartDoc(_dc, &info) <= 0)
            {
                throw PrintDeviceException.FromLastError("StartDoc");
            }
        }

        _documentStarted = true;
    }

    public void StartPage()
    {
        if (PInvoke.StartPage(_dc) <= 0)
        {
            throw PrintDeviceException.FromLastError("StartPage");
        }
    }

    public unsafe void DrawBand(ReadOnlySpan<byte> bgra, int width, int height, int stride, int destX, int destY, int destWidth, int destHeight)
    {
        if (width < 1 || height < 1 || stride != width * 4 || bgra.Length < stride * height)
        {
            throw new ArgumentException("The band does not match its dimensions.");
        }

        var info = new BITMAPINFO();
        info.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
        info.bmiHeader.biWidth = width;
        info.bmiHeader.biHeight = -height; // top-down
        info.bmiHeader.biPlanes = 1;
        info.bmiHeader.biBitCount = 32;
        info.bmiHeader.biCompression = 0; // BI_RGB
        if (destWidth != width || destHeight != height)
        {
            _ = PInvoke.SetStretchBltMode(_dc, STRETCH_BLT_MODE.HALFTONE);
        }

        fixed (byte* bits = bgra)
        {
            var rows = PInvoke.StretchDIBits(_dc, destX, destY, destWidth, destHeight, 0, 0, width, height, bits, &info, DIB_USAGE.DIB_RGB_COLORS, ROP_CODE.SRCCOPY);
            if (rows <= 0)
            {
                throw PrintDeviceException.FromLastError("StretchDIBits");
            }
        }
    }

    public void EndPage()
    {
        if (PInvoke.EndPage(_dc) <= 0)
        {
            throw PrintDeviceException.FromLastError("EndPage");
        }
    }

    public void EndDocument()
    {
        _documentEnded = true;
        if (PInvoke.EndDoc(_dc) <= 0)
        {
            throw PrintDeviceException.FromLastError("EndDoc");
        }
    }

    public void AbortDocument()
    {
        if (_documentStarted && !_documentEnded)
        {
            _documentEnded = true;
            _ = PInvoke.AbortDoc(_dc);
        }
    }

    public void RequestAbort() => Volatile.Write(ref _abortRequested, 1);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        AbortDocument(); // a job that was neither ended nor aborted must not be left in the spooler
        Devices.TryRemove(_key, out _);
        PInvoke.DeleteDC(_dc);
    }

    // The spooler calls this during a long GDI call. FALSE cancels the job, which is how a cancel reaches a call that is blocked in the spooler.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe BOOL AbortProcedure(HDC dc, int error) =>
        Devices.TryGetValue((nint)dc.Value, out var device) && Volatile.Read(ref device._abortRequested) != 0 ? false : true;
}
