using System.Runtime.InteropServices;
using System.Text;

namespace S12;

/// <summary>DEVMODE handling through the driver (DocumentProperties), device capabilities, PrintTicket capabilities.</summary>
internal static unsafe class Spooler
{
    public static List<string> EnumPrinters()
    {
        var list = new List<string>();
        Native.EnumPrintersW(0x2 | 0x4, null, 4, null, 0, out var need, out _);   // PRINTER_ENUM_LOCAL | CONNECTIONS, level 4 (name + attributes)
        if (need == 0) return list;
        var buf = Marshal.AllocHGlobal(need);
        try
        {
            if (Native.EnumPrintersW(0x2 | 0x4, null, 4, (byte*)buf, need, out _, out var n))
                for (var i = 0; i < n; i++) { var p = Marshal.ReadIntPtr(buf, i * 24); list.Add(Marshal.PtrToStringUni(p) ?? ""); }   // PRINTER_INFO_4W: pPrinterName, pServerName, Attributes (24 bytes on x64)
        }
        finally { Marshal.FreeHGlobal(buf); }
        return list;
    }

    public sealed class DevMode
    {
        public byte[] Bytes = [];
        public short Get16(int o) => BitConverter.ToInt16(Bytes, o);
        public void Set16(int o, short v) => BitConverter.TryWriteBytes(Bytes.AsSpan(o), v);
        public int Fields { get => BitConverter.ToInt32(Bytes, Native.O_Fields); set => BitConverter.TryWriteBytes(Bytes.AsSpan(Native.O_Fields), value); }
        public void Apply(int field, int offset, short value) { Fields |= field; Set16(offset, value); }
        public string Describe() => $"fields=0x{Fields:X} orientation={Get16(Native.O_Orientation)} paper={Get16(Native.O_PaperSize)} copies={Get16(Native.O_Copies)} duplex={Get16(Native.O_Duplex)} collate={Get16(Native.O_Collate)} colour={Get16(Native.O_Color)} quality={Get16(Native.O_PrintQuality)} yres={Get16(Native.O_YRes)} size={Bytes.Length}";
    }

    /// <summary>The driver's default DEVMODE for the printer (no UI).</summary>
    public static DevMode Default(string printer)
    {
        var size = Native.DocumentPropertiesW(0, 0, printer, null, null, 0);
        if (size <= 0) throw new InvalidOperationException("DocumentProperties size query failed " + Marshal.GetLastWin32Error());
        var dm = new DevMode { Bytes = new byte[size] };
        fixed (byte* p = dm.Bytes) if (Native.DocumentPropertiesW(0, 0, printer, p, null, Native.DM_OUT_BUFFER) < 0) throw new InvalidOperationException("DocumentProperties failed");
        return dm;
    }

    /// <summary>Ask the driver to merge and validate our requested settings. What it leaves set is what it accepted.</summary>
    public static DevMode Validate(string printer, DevMode requested)
    {
        var o = new DevMode { Bytes = new byte[requested.Bytes.Length] };
        fixed (byte* po = o.Bytes) fixed (byte* pi = requested.Bytes)
            if (Native.DocumentPropertiesW(0, 0, printer, po, pi, Native.DM_IN_BUFFER | Native.DM_OUT_BUFFER) < 0) throw new InvalidOperationException("DocumentProperties merge failed");
        return o;
    }

    public static string Capabilities(string printer)
    {
        var sb = new StringBuilder();
        int Cap(short c, byte* buf = null) => Native.DeviceCapabilitiesW(printer, "", c, buf, null);
        sb.AppendLine($"DC_DUPLEX={Cap(Native.DC_DUPLEX)} (1 = duplex supported)");
        sb.AppendLine($"DC_COLLATE={Cap(Native.DC_COLLATE)}");
        sb.AppendLine($"DC_COPIES(max)={Cap(Native.DC_COPIES)}");
        sb.AppendLine($"DC_COLORDEVICE={Cap(Native.DC_COLORDEVICE)}");
        sb.AppendLine($"DC_FIELDS=0x{Cap(Native.DC_FIELDS):X}");
        var nres = Cap(Native.DC_ENUMRESOLUTIONS);
        if (nres > 0) { var b = stackalloc int[nres * 2]; Native.DeviceCapabilitiesW(printer, "", Native.DC_ENUMRESOLUTIONS, (byte*)b, null); sb.Append("resolutions: "); for (var i = 0; i < nres; i++) sb.Append($"{b[i * 2]}x{b[i * 2 + 1]} "); sb.AppendLine(); }
        else sb.AppendLine($"DC_ENUMRESOLUTIONS={nres}");
        var np = Cap(Native.DC_PAPERS);
        if (np > 0) { var b = stackalloc short[np]; Native.DeviceCapabilitiesW(printer, "", Native.DC_PAPERS, (byte*)b, null); sb.Append($"papers({np}): "); for (var i = 0; i < Math.Min(np, 12); i++) sb.Append(b[i]).Append(' '); sb.AppendLine(); }
        return sb.ToString();
    }

    /// <summary>The Print Schema capabilities XML for the printer (prntvpt). Read-only, no UI.</summary>
    public static string PrintCapabilitiesXml(string printer)
    {
        var hr = Native.PTOpenProvider(printer, 1, out var prov);
        if (hr < 0) return $"PTOpenProvider 0x{hr:X8}";
        try
        {
            Native.CreateStreamOnHGlobal(0, true, out var stream);
            hr = Native.PTGetPrintCapabilities(prov, 0, stream, out var err);
            if (hr < 0) return $"PTGetPrintCapabilities 0x{hr:X8}";
            return Native.ReadStream(stream);
        }
        finally { Native.PTCloseProvider(prov); }
    }
}
