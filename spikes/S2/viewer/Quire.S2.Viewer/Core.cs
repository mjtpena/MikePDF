using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Quire.S2.Viewer;

/// <summary>Command line: --key value pairs (flags without a value are "1").</summary>
public sealed class Options
{
    readonly Dictionary<string, string> _d = new(StringComparer.OrdinalIgnoreCase);
    public Options(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) continue;
            string k = args[i][2..];
            string v = (i + 1 < args.Length && !args[i + 1].StartsWith("--")) ? args[++i] : "1";
            _d[k] = v;
        }
    }
    public string Str(string k, string def) => _d.TryGetValue(k, out var v) ? v : def;
    public int Int(string k, int def) => _d.TryGetValue(k, out var v) && int.TryParse(v, out var i) ? i : def;
    public double Dbl(string k, double def) => _d.TryGetValue(k, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : def;
    public bool Flag(string k, bool def) => _d.TryGetValue(k, out var v) ? v != "0" && !v.Equals("false", StringComparison.OrdinalIgnoreCase) : def;
    public override string ToString() => string.Join(' ', _d.Select(kv => $"--{kv.Key} {kv.Value}"));
}

public static class Clock2
{
    public static long Now() => System.Diagnostics.Stopwatch.GetTimestamp();
    public static double Ms(long ticks) => ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    public static double MsSince(long t0) => Ms(Now() - t0);
}

public sealed record Stat(int N, double P50, double P95, double P99, double Max, double Mean, double Min)
{
    public static Stat Of(IReadOnlyList<double> v)
    {
        if (v.Count == 0) return new Stat(0, 0, 0, 0, 0, 0, 0);
        var s = v.OrderBy(x => x).ToArray();
        double Pct(double p) { double idx = p * (s.Length - 1); int lo = (int)idx; int hi = Math.Min(lo + 1, s.Length - 1); return s[lo] + (s[hi] - s[lo]) * (idx - lo); }
        return new Stat(s.Length, Pct(0.5), Pct(0.95), Pct(0.99), s[^1], s.Average(), s[0]);
    }
    public string Row() => $"n={N} p50={P50:F3} p95={P95:F3} p99={P99:F3} max={Max:F3} mean={Mean:F3}";
}

/// <summary>Simple thread-safe sample bag.</summary>
public sealed class Samples
{
    readonly List<double> _v = new();
    public void Add(double x) { lock (_v) _v.Add(x); }
    public Stat Stat() { lock (_v) return Quire.S2.Viewer.Stat.Of(_v); }
    public double[] ToArray() { lock (_v) return _v.ToArray(); }
    public int Count { get { lock (_v) return _v.Count; } }
    public void Clear() { lock (_v) _v.Clear(); }
}

/// <summary>DXGI adapter enumeration and IDXGIAdapter3::QueryVideoMemoryInfo (per-process usage), via raw COM vtables (no extra dependency).</summary>
public static unsafe class Dxgi
{
    [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(Guid* riid, void** ppFactory);
    static readonly Guid IID_Factory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    static readonly Guid IID_Adapter3 = new("645967A4-1392-4310-A798-8053CE3E93FD");

    public sealed record AdapterInfo(int Index, string Name, uint VendorId, ulong DedicatedVideoMB, ulong SharedSystemMB, long Luid, uint Flags);
    public sealed record MemInfo(int Index, string Name, double LocalUsageMB, double NonLocalUsageMB, double LocalBudgetMB, double NonLocalBudgetMB);

    static void* Vt(void* obj, int idx) => (*(void***)obj)[idx];

    public static List<AdapterInfo> Adapters()
    {
        var list = new List<AdapterInfo>();
        void* f; var iid = IID_Factory1;
        if (CreateDXGIFactory1(&iid, &f) < 0) return list;
        for (uint i = 0; ; i++)
        {
            void* a;
            if (((delegate* unmanaged[Stdcall]<void*, uint, void**, int>)Vt(f, 12))(f, i, &a) < 0) break;   // IDXGIFactory1::EnumAdapters1
            byte* desc = stackalloc byte[320];
            ((delegate* unmanaged[Stdcall]<void*, byte*, int>)Vt(a, 10))(a, desc);                          // IDXGIAdapter1::GetDesc1
            string name = new string((char*)desc).TrimEnd('\0');
            uint vendor = *(uint*)(desc + 256);
            ulong ded = *(ulong*)(desc + 272), shared = *(ulong*)(desc + 288);
            long luid = *(long*)(desc + 296); uint flags = *(uint*)(desc + 304);
            list.Add(new AdapterInfo((int)i, name, vendor, ded >> 20, shared >> 20, luid, flags));
            ((delegate* unmanaged[Stdcall]<void*, uint>)Vt(a, 2))(a);
        }
        ((delegate* unmanaged[Stdcall]<void*, uint>)Vt(f, 2))(f);
        return list;
    }

    /// <summary>Per-process video memory usage on every hardware adapter (local = dedicated segment, non-local = shared system memory).</summary>
    public static List<MemInfo> Query()
    {
        var res = new List<MemInfo>();
        void* f; var iid = IID_Factory1;
        if (CreateDXGIFactory1(&iid, &f) < 0) return res;
        for (uint i = 0; ; i++)
        {
            void* a;
            if (((delegate* unmanaged[Stdcall]<void*, uint, void**, int>)Vt(f, 12))(f, i, &a) < 0) break;
            byte* desc = stackalloc byte[320];
            ((delegate* unmanaged[Stdcall]<void*, byte*, int>)Vt(a, 10))(a, desc);
            string name = new string((char*)desc).TrimEnd('\0'); uint flags = *(uint*)(desc + 304);
            void* a3; var iid3 = IID_Adapter3;
            if (((delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)Vt(a, 0))(a, &iid3, &a3) >= 0 && (flags & 2) == 0)   // skip DXGI_ADAPTER_FLAG_SOFTWARE
            {
                ulong* l = stackalloc ulong[4]; ulong* n = stackalloc ulong[4];
                var q = (delegate* unmanaged[Stdcall]<void*, uint, int, ulong*, int>)Vt(a3, 14);                   // IDXGIAdapter3::QueryVideoMemoryInfo
                int h1 = q(a3, 0, 0, l), h2 = q(a3, 0, 1, n);
                if (h1 >= 0 && h2 >= 0) res.Add(new MemInfo((int)i, name, l[1] / 1048576.0, n[1] / 1048576.0, l[0] / 1048576.0, n[0] / 1048576.0));
                ((delegate* unmanaged[Stdcall]<void*, uint>)Vt(a3, 2))(a3);
            }
            ((delegate* unmanaged[Stdcall]<void*, uint>)Vt(a, 2))(a);
        }
        ((delegate* unmanaged[Stdcall]<void*, uint>)Vt(f, 2))(f);
        return res;
    }
}

public static unsafe class Sys
{
    [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo")] static extern int K32GetProcessMemoryInfo(nint h, void* c, uint cb);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int n);
    [DllImport("user32.dll", CharSet = CharSet.Ansi)] static extern bool EnumDisplaySettingsA(string? dev, int mode, byte* dm);
    [DllImport("kernel32.dll")] static extern int GetSystemTimes(ulong* idle, ulong* kernel, ulong* user);

    /// <summary>(working set MB, private bytes MB) of this process.</summary>
    public static (double ws, double priv) ProcessMem()
    {
        byte* b = stackalloc byte[96]; *(uint*)b = 80;
        if (K32GetProcessMemoryInfo(System.Diagnostics.Process.GetCurrentProcess().Handle, b, 80) == 0) return (0, 0);
        return (*(nuint*)(b + 16) / 1048576.0, *(nuint*)(b + 72) / 1048576.0);   // WorkingSetSize, PrivateUsage (PROCESS_MEMORY_COUNTERS_EX)
    }

    public static int PrimaryRefreshHz()
    {
        byte* dm = stackalloc byte[220]; new Span<byte>(dm, 220).Clear(); *(short*)(dm + 36) = 156;   // DEVMODEA.dmSize
        return EnumDisplaySettingsA(null, -1, dm) ? *(int*)(dm + 120) : 0;                              // dmDisplayFrequency
    }
    public static (int w, int h) PrimaryRes() { return (GetSystemMetrics(0), GetSystemMetrics(1)); }

    static ulong _i0, _k0, _u0;
    public static void CpuBegin() { ulong i, k, u; GetSystemTimes(&i, &k, &u); _i0 = i; _k0 = k; _u0 = u; }
    public static double CpuPercentSinceBegin() { ulong i, k, u; GetSystemTimes(&i, &k, &u); double idle = i - _i0, tot = (k - _k0) + (u - _u0); return tot > 0 ? 100.0 * (1 - idle / tot) : 0; }
}

public static unsafe class Dwm
{
    [StructLayout(LayoutKind.Sequential)] struct UR { public uint Num, Den; }
    [StructLayout(LayoutKind.Sequential)]
    struct TimingInfo
    {
        public uint cbSize; public UR rateRefresh; public ulong qpcRefreshPeriod; public UR rateCompose; public ulong qpcVBlank; public ulong cRefresh; public uint cDXRefresh; public ulong qpcCompose; public ulong cFrame; public uint cDXPresent;
        public ulong cRefreshFrame, cFrameSubmitted; public uint cDXPresentSubmitted; public ulong cFrameConfirmed; public uint cDXPresentConfirmed; public ulong cRefreshConfirmed; public uint cDXRefreshConfirmed;
        public ulong cFramesLate; public uint cFramesOutstanding; public ulong cFrameDisplayed, qpcFrameDisplayed, cRefreshFrameDisplayed, cFrameComplete, qpcFrameComplete, cFramePending, qpcFramePending, cFramesDisplayed, cFramesComplete, cFramesPending, cFramesAvailable, cFramesDropped, cFramesMissed, cRefreshNextDisplayed, cRefreshNextPresented, cRefreshStarted, cPixelsReceived, cPixelsDrawn, cBuffersEmpty;
    }
    [DllImport("dwmapi.dll")] static extern int DwmGetCompositionTimingInfo(nint hwnd, ref TimingInfo ti);
    public sealed record Snapshot(int Hr, double RefreshHz, double RefreshPeriodMs, ulong Late, ulong Dropped, ulong Missed, ulong Displayed, ulong Refreshes);
    public static Snapshot Get(nint hwnd)
    {
        var ti = new TimingInfo { cbSize = (uint)sizeof(TimingInfo) };
        int hr = DwmGetCompositionTimingInfo(hwnd, ref ti);
        if (hr != 0) return new Snapshot(hr, 0, 0, 0, 0, 0, 0, 0);
        double hz = ti.rateRefresh.Den == 0 ? 0 : (double)ti.rateRefresh.Num / ti.rateRefresh.Den;
        return new Snapshot(0, hz, ti.qpcRefreshPeriod * 1000.0 / System.Diagnostics.Stopwatch.Frequency, ti.cFramesLate, ti.cFramesDropped, ti.cFramesMissed, ti.cFramesDisplayed, ti.cRefresh);
    }
}

public static class Csv
{
    public static void Write(string path, IEnumerable<string> lines) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllLines(path, lines, new UTF8Encoding(false)); }
    public static string F(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);
}
