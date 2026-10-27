using System.Diagnostics;
using System.Text;

namespace HelloQuire;

/// <summary>QPC-based milestone log. T0 is a Stopwatch.GetTimestamp() value the bench harness takes right
/// before CreateProcess and passes in QUIRE_T0 (QPC is a machine-wide clock, so values are comparable).</summary>
internal static class Timing
{
    private static readonly long T0 = long.TryParse(Environment.GetEnvironmentVariable("QUIRE_T0"), out var t) ? t : 0;
    private static readonly List<(string Name, long Ticks)> Marks = new();
    private static readonly object Gate = new();

    public static bool Enabled => T0 != 0;

    public static void Mark(string name)
    {
        long now = Stopwatch.GetTimestamp();
        lock (Gate) Marks.Add((name, now));
    }

    public static string Dump()
    {
        var sb = new StringBuilder();
        lock (Gate)
        {
            foreach (var (name, ticks) in Marks)
            {
                double ms = T0 == 0 ? 0 : (ticks - T0) * 1000.0 / Stopwatch.Frequency;
                sb.Append(name).Append(',').Append(ms.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
            }
        }
        return sb.ToString();
    }

    public static void WriteTo(string path) => File.WriteAllText(path, Dump());
}

internal static class Log
{
    private static string? _path;
    public static string? Path
    {
        get
        {
            if (_path != null) return _path;
            var a = Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length - 1; i++) if (a[i] == "--log") return _path = a[i + 1];
            return _path = Environment.GetEnvironmentVariable("QUIRE_LOG") ?? "";
        }
    }
    public static string? Arg(string name)
    {
        var a = Environment.GetCommandLineArgs();
        for (int i = 0; i < a.Length - 1; i++) if (a[i] == name) return a[i + 1];
        return null;
    }
    public static bool Flag(string name) => Environment.GetCommandLineArgs().Contains(name);
    public static void Write(string line)
    {
        var p = Path;
        if (string.IsNullOrEmpty(p)) return;
        try { File.AppendAllText(p, $"{DateTime.Now:HH:mm:ss.fff} [{Environment.ProcessId}] {line}\n"); } catch { }
    }
}
