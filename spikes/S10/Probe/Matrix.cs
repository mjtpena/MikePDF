// S10 matrix: holder configuration (how the broker and the engine hold the document) x replace mechanism.
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace S10;

internal sealed record Holder(string Name, uint? BrokerShare, bool KeepBroker, bool View, bool ViewInChild);

internal static class Matrix
{
    public const int FileSize = 256 * 1024;

    public static readonly Holder[] Holders =
    [
        new("none", null, false, false, false),
        new("broker-handle R (ADR-0005 flags, no view)", N.FILE_SHARE_READ, true, false, false),
        new("broker-handle R|D (no view)", N.FILE_SHARE_READ | N.FILE_SHARE_DELETE, true, false, false),
        new("broker R + engine view (ADR-0005 as built)", N.FILE_SHARE_READ, true, true, false),
        new("broker R|D + engine view", N.FILE_SHARE_READ | N.FILE_SHARE_DELETE, true, true, false),
        new("broker R|W|D + engine view", N.FILE_SHARE_READ | N.FILE_SHARE_WRITE | N.FILE_SHARE_DELETE, true, true, false),
        new("engine view only, broker closed (opened R)", N.FILE_SHARE_READ, false, true, false),
        new("engine view only, broker closed (opened R|D)", N.FILE_SHARE_READ | N.FILE_SHARE_DELETE, false, true, false),
        new("broker R + view in ENGINE PROCESS", N.FILE_SHARE_READ, true, true, true),
        new("broker R|D + view in ENGINE PROCESS", N.FILE_SHARE_READ | N.FILE_SHARE_DELETE, true, true, true),
    ];

    public static readonly string[] Mechanisms =
    [
        "ReplaceFileW",
        "ReplaceFileW+backup",
        "MoveFileEx(REPLACE_EXISTING)",
        "RenameEx(REPLACE|POSIX)",
        "RenameEx(REPLACE)",
        "RenameInfo(ReplaceIfExists)",
        "swap: rename target away, rename temp in",
        "delete target (POSIX), rename temp in",
    ];

    public static byte[] Content(byte seed)
    {
        var b = new byte[FileSize];
        var head = Encoding.ASCII.GetBytes("%PDF-1.7\n");
        head.CopyTo(b, 0);
        for (var i = head.Length; i < b.Length; i++) b[i] = (byte)(seed + (i * 31));
        return b;
    }

    public static string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b))[..12];

    /// <summary>Repeats a few (holder, mechanism) pairs to expose flakiness; prints a count of outcomes.</summary>
    public static void Repeat(string root, int n, string? outFile)
    {
        var a = Content(1);
        var b = Content(2);
        var rows = new List<Dictionary<string, object?>>();
        foreach (var hi in new[] { 4, 9, 3, 8 })
        {
            foreach (var mech in new[] { Mechanisms[0], Mechanisms[1], Mechanisms[3], Mechanisms[6], Mechanisms[7] })
            {
                var counts = new SortedDictionary<string, int>();
                for (var i = 0; i < n; i++)
                {
                    var dir = Path.Combine(root, $"r{hi}-{i}");
                    Directory.CreateDirectory(dir);
                    var r = Scenario(dir, Holders[hi], mech, a, b, Hash(a), Hash(b));
                    var key = r["ok"] is true ? "OK" : "fail " + System.Text.RegularExpressions.Regex.Match(r["steps"]?.ToString() ?? "", @"(\d+) \(").Groups[1].Value;
                    counts[key] = counts.GetValueOrDefault(key) + 1;
                    try { Directory.Delete(dir, true); } catch { }
                }

                var row = new Dictionary<string, object?> { ["holder"] = Holders[hi].Name, ["mechanism"] = mech, ["runs"] = n, ["outcomes"] = string.Join(", ", counts.Select(kv => $"{kv.Key} x{kv.Value}")) };
                rows.Add(row);
                Console.WriteLine($"{Holders[hi].Name,-45} | {mech,-42} | {row["outcomes"]}");
            }
        }

        if (outFile != null) File.AppendAllLines(outFile, rows.Select(x => JsonSerializer.Serialize(x)));
    }

    public static void Run(string root, string label, string? outFile)
    {
        var results = new List<Dictionary<string, object?>>();
        var a = Content(1);
        var b = Content(2);
        var ha = Hash(a);
        var hb = Hash(b);
        Console.WriteLine($"# matrix root={root} label={label} A={ha} B={hb}");
        var n = 0;
        var subset = Environment.GetEnvironmentVariable("S10_SUBSET") == "1";   // cloud/network folders: fewer files, same questions
        foreach (var holder in Holders.Where(h => !subset || h.Name.StartsWith("none") || h.Name.StartsWith("broker R + engine view") || h.Name.StartsWith("broker R|D + engine view") || h.Name.StartsWith("broker R|D + view in ENGINE")))
        {
            foreach (var mech in Mechanisms.Where(m => !subset || m is "ReplaceFileW" or "MoveFileEx(REPLACE_EXISTING)" or "RenameEx(REPLACE|POSIX)" or "swap: rename target away, rename temp in"))
            {
                var dir = Path.Combine(root, $"m{n++:D3}");
                Directory.CreateDirectory(dir);
                var r = Scenario(dir, holder, mech, a, b, ha, hb);
                r["root"] = label;
                r["holder"] = holder.Name;
                r["mechanism"] = mech;
                results.Add(r);
                Console.WriteLine($"{holder.Name,-52} | {mech,-42} | {(r["ok"] is true ? "OK  " : "FAIL")} | {r["steps"]} | target={r["targetAfter"]} viewSees={r["viewSees"]} leftovers={r["leftovers"]}");
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        if (outFile != null)
            File.AppendAllLines(outFile, results.Select(x => JsonSerializer.Serialize(x)));
    }

    private static Dictionary<string, object?> Scenario(string dir, Holder holder, string mech, byte[] a, byte[] b, string ha, string hb)
    {
        var target = Path.Combine(dir, "doc.pdf");
        var temp = Path.Combine(dir, ".doc.pdf.mikepdf-0a1b2c3d.tmp");
        File.WriteAllBytes(target, a);
        File.WriteAllBytes(temp, b);
        File.SetAttributes(temp, FileAttributes.Hidden);

        SafeFileHandle? brokerHandle = null;
        EngineView? view = null;
        Process? child = null;
        StreamWriter? childIn = null;
        StreamReader? childOut = null;
        var steps = new List<string>();
        var ok = false;
        try
        {
            if (holder.BrokerShare is { } share)
            {
                var (h, e) = N.Open(target, N.GENERIC_READ, share);
                if (h is null) throw new InvalidOperationException($"holder open failed: {N.Err(e)}");
                brokerHandle = h;
            }

            if (holder.View)
            {
                if (holder.ViewInChild)
                {
                    child = StartChild();
                    childIn = child.StandardInput;
                    childOut = child.StandardOutput;
                    if (childOut.ReadLine() != "READY") throw new InvalidOperationException("child not ready");
                    if (!N.DuplicateHandle(N.GetCurrentProcess(), brokerHandle!.DangerousGetHandle(), child.Handle, out var remote, 0, false, 2))
                        throw new System.ComponentModel.Win32Exception();
                    childIn.WriteLine(remote.ToInt64());
                    var line = childOut.ReadLine();
                    if (line is null || !line.StartsWith("MAPPED")) throw new InvalidOperationException("child map failed: " + line);
                }
                else
                {
                    view = new EngineView(EngineView.DuplicateInProcess(brokerHandle!));
                }

                if (!holder.KeepBroker)
                {
                    brokerHandle!.Dispose();
                    brokerHandle = null;
                }
            }

            ok = Apply(mech, target, temp, dir, steps);
        }
        catch (Exception ex)
        {
            steps.Add("EXC " + ex.Message);
        }

        // What the holders see afterwards, before they let go.
        string viewSees = "n/a";
        try
        {
            if (view != null) viewSees = Hash(view.ReadAll()) == ha ? "A(old, intact)" : "CHANGED";
            else if (child != null)
            {
                childIn!.WriteLine("READ");
                var l = childOut!.ReadLine() ?? "";
                viewSees = l.EndsWith(ha) ? "A(old, intact)" : "CHANGED:" + l;
            }
            else if (brokerHandle != null)
            {
                var buf = new byte[FileSize];
                var read = RandomAccess.Read(brokerHandle, buf, 0);
                viewSees = read == FileSize && Hash(buf) == ha ? "A(old, intact)" : $"CHANGED(read {read})";
            }
        }
        catch (Exception ex) { viewSees = "EXC " + ex.Message; }

        var targetAfter = "missing";
        try { if (File.Exists(target)) { var h = Hash(File.ReadAllBytes(target)); targetAfter = h == ha ? "A" : h == hb ? "B" : "OTHER"; } } catch (Exception ex) { targetAfter = "unreadable:" + ex.Message; }
        var leftovers = string.Join(",", Directory.GetFileSystemEntries(dir).Select(Path.GetFileName).Where(f => f != "doc.pdf").Select(f => f!.StartsWith(".doc.pdf.mikepdf") ? "tmp" : f));

        // Release everything, then see what cleanup can do (the .old of the swap).
        view?.Dispose();
        brokerHandle?.Dispose();
        if (child != null)
        {
            try { childIn!.WriteLine("EXIT"); child.WaitForExit(3000); } catch { }
            if (!child.HasExited) child.Kill();
            child.Dispose();
        }

        var cleanup = "";
        var old = Path.Combine(dir, "doc.pdf.old");
        if (File.Exists(old))
        {
            try { File.Delete(old); cleanup = "old deleted after release"; } catch (Exception ex) { cleanup = "old NOT deleted: " + ex.Message; }
        }

        return new Dictionary<string, object?>
        {
            ["ok"] = ok,
            ["steps"] = string.Join("; ", steps),
            ["targetAfter"] = targetAfter,
            ["viewSees"] = viewSees,
            ["leftovers"] = leftovers == "" ? "-" : leftovers,
            ["cleanup"] = cleanup,
        };
    }

    private static bool Apply(string mech, string target, string temp, string dir, List<string> steps)
    {
        switch (mech)
        {
            case "ReplaceFileW":
                return Step(steps, "ReplaceFileW", N.ReplaceFileW(target, temp, null, 0, IntPtr.Zero, IntPtr.Zero));
            case "ReplaceFileW+backup":
                return Step(steps, "ReplaceFileW", N.ReplaceFileW(target, temp, Path.Combine(dir, "doc.pdf.bak"), 0, IntPtr.Zero, IntPtr.Zero));
            case "MoveFileEx(REPLACE_EXISTING)":
                return Step(steps, "MoveFileEx", N.MoveFileExW(temp, target, N.MOVEFILE_REPLACE_EXISTING | N.MOVEFILE_WRITE_THROUGH));
            case "RenameEx(REPLACE|POSIX)":
                return Rename(steps, temp, target, N.FILE_RENAME_FLAG_REPLACE_IF_EXISTS | N.FILE_RENAME_FLAG_POSIX_SEMANTICS, true);
            case "RenameEx(REPLACE)":
                return Rename(steps, temp, target, N.FILE_RENAME_FLAG_REPLACE_IF_EXISTS, true);
            case "RenameInfo(ReplaceIfExists)":
                return Rename(steps, temp, target, N.FILE_RENAME_FLAG_REPLACE_IF_EXISTS, false);
            case "swap: rename target away, rename temp in":
            {
                var old = Path.Combine(dir, "doc.pdf.old");
                if (!Step(steps, "target->.old", N.MoveFileExW(target, old, 0))) return false;
                if (!Step(steps, ".tmp->target", N.MoveFileExW(temp, target, 0)))
                {
                    // Put the original back, as a real implementation would.
                    Step(steps, "rollback .old->target", N.MoveFileExW(old, target, 0));
                    return false;
                }

                // Deleting the old file while holders still map it:
                if (!Step(steps, "delete .old", N.DeleteFileW(old)))
                {
                    var (h, e) = N.Open(old, N.DELETE | N.FILE_READ_ATTRIBUTES, N.FILE_SHARE_READ | N.FILE_SHARE_WRITE | N.FILE_SHARE_DELETE);
                    if (h is null) steps.Add($"open .old for POSIX delete: {N.Err(e)}");
                    else
                    {
                        using (h) Step(steps, "POSIX delete .old", N.DisposeByHandle(h, N.FILE_DISPOSITION_FLAG_DELETE | N.FILE_DISPOSITION_FLAG_POSIX_SEMANTICS) == 0);
                    }
                }

                return true;
            }
            case "delete target (POSIX), rename temp in":
            {
                var (h, e) = N.Open(target, N.DELETE | N.FILE_READ_ATTRIBUTES, N.FILE_SHARE_READ | N.FILE_SHARE_WRITE | N.FILE_SHARE_DELETE);
                if (h is null) { steps.Add($"open target for delete: {N.Err(e)}"); return false; }
                using (h)
                {
                    if (!Step(steps, "POSIX delete target", N.DisposeByHandle(h, N.FILE_DISPOSITION_FLAG_DELETE | N.FILE_DISPOSITION_FLAG_POSIX_SEMANTICS) == 0)) return false;
                }

                return Step(steps, ".tmp->target", N.MoveFileExW(temp, target, 0));
            }
            default:
                throw new ArgumentException(mech);
        }
    }

    private static bool Rename(List<string> steps, string temp, string target, uint flags, bool ex)
    {
        // The temp file is the one we own: open it with DELETE access (required to rename it) and full sharing.
        var (h, e) = N.Open(temp, N.DELETE | N.GENERIC_READ, N.FILE_SHARE_READ | N.FILE_SHARE_WRITE | N.FILE_SHARE_DELETE);
        if (h is null) { steps.Add($"open temp: {N.Err(e)}"); return false; }
        using (h)
        {
            var err = N.RenameByHandle(h, target, flags, ex);
            steps.Add(err == 0 ? "SetFileInformationByHandle OK" : "SetFileInformationByHandle " + N.Err(err));
            return err == 0;
        }
    }

    private static bool Step(List<string> steps, string name, bool ok)
    {
        if (ok) steps.Add(name + " OK");
        else steps.Add(name + " " + N.Err(System.Runtime.InteropServices.Marshal.GetLastWin32Error()));
        return ok;
    }

    private static Process StartChild()
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!, "hold")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        return Process.Start(psi)!;
    }

    /// <summary>The child: stands in for the engine process. Receives a duplicated file handle value, maps it as the engine does, closes it, and holds the view.</summary>
    public static void HoldMain()
    {
        Console.Out.WriteLine("READY");
        Console.Out.Flush();
        var value = long.Parse(Console.In.ReadLine()!);
        var view = new EngineView(new SafeFileHandle((IntPtr)value, true));
        Console.Out.WriteLine("MAPPED " + view.Length);
        Console.Out.Flush();
        while (Console.In.ReadLine() is { } line)
        {
            if (line == "READ") { Console.Out.WriteLine("HASH " + Hash(view.ReadAll())); Console.Out.Flush(); }
            else if (line == "EXIT") break;
        }

        view.Dispose();
    }
}
