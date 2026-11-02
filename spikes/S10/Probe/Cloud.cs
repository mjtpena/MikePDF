// S10 item 4: replace behaviour inside a OneDrive-synced folder (Files On-Demand). Observed from the file system only: attributes, reparse state, conflict copies, error codes.
// OneDrive's server-side version history needs an authenticated API and is NOT observable from here (UNVERIFIED).
using System.Diagnostics;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace S10;

internal static class Cloud
{
    private const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x400, PINNED = 0x80000, UNPINNED = 0x100000, RECALL_ON_OPEN = 0x40000, RECALL_ON_DATA_ACCESS = 0x400000, OFFLINE = 0x1000, SPARSE = 0x200;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static StreamWriter? _log;

    private static void Log(string s)
    {
        var line = $"[{Clock.Elapsed.TotalSeconds,7:0.0}s] {s}";
        Console.WriteLine(line);
        _log?.WriteLine(line);
        _log?.Flush();
    }

    private static string Attrs(string path)
    {
        var a = Raw(path);
        if (a == 0xFFFFFFFF) return "missing";
        var parts = new List<string>();
        if ((a & FILE_ATTRIBUTE_REPARSE_POINT) != 0) parts.Add("reparse(placeholder)");
        if ((a & PINNED) != 0) parts.Add("pinned");
        if ((a & UNPINNED) != 0) parts.Add("unpinned");
        if ((a & RECALL_ON_DATA_ACCESS) != 0) parts.Add("recall-on-data-access(online-only)");
        if ((a & RECALL_ON_OPEN) != 0) parts.Add("recall-on-open");
        if ((a & OFFLINE) != 0) parts.Add("offline");
        if ((a & SPARSE) != 0) parts.Add("sparse");
        if ((a & 2) != 0) parts.Add("hidden");
        return parts.Count == 0 ? "plain local file" : string.Join("+", parts);
    }

    // File.GetAttributes (FindFirstFile-style, what PowerShell and Explorer show). GetFileAttributesW from this process did not show the reparse bit of a placeholder that PowerShell showed, see the report.
    // Enumeration-based (FindFirstFile) because GetFileAttributes(Ex) from this process does not show the placeholder's reparse bit while PowerShell's does (see report).
    public static uint Raw(string p)
    {
        try
        {
            var fi = new DirectoryInfo(Path.GetDirectoryName(p)!).EnumerateFileSystemInfos(Path.GetFileName(p)).FirstOrDefault();
            return fi is null ? 0xFFFFFFFF : (uint)fi.Attributes;
        }
        catch { return 0xFFFFFFFF; }
    }

    private static bool IsPlaceholder(string path) => (Raw(path) & FILE_ATTRIBUTE_REPARSE_POINT) != 0 && Raw(path) != 0xFFFFFFFF;

    /// <summary>Seconds until the file becomes a sync placeholder (OneDrive converts a local file after it has uploaded it), or -1.</summary>
    private static double WaitSynced(string path, int timeoutSeconds)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < timeoutSeconds)
        {
            if (IsPlaceholder(path)) return sw.Elapsed.TotalSeconds;
            Thread.Sleep(500);
        }

        return -1;
    }

    private static string[] Siblings(string dir) => Directory.GetFileSystemEntries(dir).Select(Path.GetFileName).Select(x => x!).OrderBy(x => x, StringComparer.Ordinal).ToArray();

    /// <summary>One save, long windows: wait for the original to finish syncing, replace it the proposed way, wait for the replacement to finish syncing, look for conflict copies.</summary>
    public static void RunFocused(string root, string logPath, int waitSeconds)
    {
        _log = new StreamWriter(logPath, false, Encoding.UTF8);
        var dir = Path.Combine(root, "MikePDF-S10-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        Log($"test folder: {dir}; waiting up to {waitSeconds}s per step");
        var a = Matrix.Content(1);
        var b = Matrix.Content(2);
        var doc = Path.Combine(dir, "doc.pdf");
        try
        {
            File.WriteAllBytes(doc, a);
            var t = WaitSynced(doc, waitSeconds);
            Log(t >= 0 ? $"original synced after {t:0.0}s ({Attrs(doc)})" : $"original not synced after {waitSeconds}s ({Attrs(doc)})");
            var (h, e) = N.Open(doc, N.GENERIC_READ, N.FILE_SHARE_READ | N.FILE_SHARE_DELETE);
            using var view = new EngineView(EngineView.DuplicateInProcess(h!));
            var temp = Path.Combine(dir, ".doc.pdf.mikepdf-0a1b2c3d.tmp");
            File.WriteAllBytes(temp, b);
            var (r, er) = N.Open(temp, N.DELETE | N.GENERIC_READ, 7);
            var err = N.RenameByHandle(r!, doc, N.FILE_RENAME_FLAG_REPLACE_IF_EXISTS | N.FILE_RENAME_FLAG_POSIX_SEMANTICS, true);
            r!.Dispose();
            Log(err == 0 ? "POSIX replace under R|D handle + mapped view: OK" : $"POSIX replace FAILED {N.Err(err)}");
            Log($"old mapped view still reads the old bytes: {Matrix.Hash(view.ReadAll()) == Matrix.Hash(a)}; path now has the new bytes: {Matrix.Hash(File.ReadAllBytes(doc)) == Matrix.Hash(b)}");
            h!.Dispose();
            var t2 = WaitSynced(doc, waitSeconds);
            Log(t2 >= 0 ? $"replacement synced after {t2:0.0}s ({Attrs(doc)})" : $"replacement not synced after {waitSeconds}s ({Attrs(doc)})");
            Log($"folder: [{string.Join(", ", Siblings(dir))}]  (a conflict copy would be named doc-<COMPUTERNAME>.pdf or doc (1).pdf)");
        }
        finally
        {
            Thread.Sleep(2000);
            foreach (var f in Directory.EnumerateFileSystemEntries(dir)) try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            try { File.SetAttributes(dir, FileAttributes.Directory); Directory.Delete(dir, true); Log("test folder deleted"); } catch (Exception ex) { Log("cleanup failed (delete it by hand): " + ex.Message); }
            _log?.Dispose();
        }
    }

    public static void Run(string root, string logPath)
    {
        _log = new StreamWriter(logPath, false, Encoding.UTF8);
        var dir = Path.Combine(root, "MikePDF-S10-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        Log($"test folder: {dir}");
        Log($"folder attributes: {Attrs(dir)}");
        var a = Matrix.Content(1);
        var b = Matrix.Content(2);
        var doc = Path.Combine(dir, "doc.pdf");
        try
        {
            File.WriteAllBytes(doc, a);
            Log($"created doc.pdf, attributes right after: {Attrs(doc)}");
            var t = WaitSynced(doc, 120);
            Log(t >= 0 ? $"doc.pdf became a sync placeholder after {t:0.0}s: {Attrs(doc)}" : $"doc.pdf did NOT become a placeholder within 120s: {Attrs(doc)} (OneDrive may be paused or offline)");

            // 1. Replace while the broker holds the file the proposed way (R|D) and the engine has it mapped.
            foreach (var (name, share, mapped, how) in new[]
            {
                ("R|D + mapped view, POSIX rename", N.FILE_SHARE_READ | N.FILE_SHARE_DELETE, true, "posix"),
                ("R|D + mapped view, ReplaceFileW", N.FILE_SHARE_READ | N.FILE_SHARE_DELETE, true, "replacefile"),
                ("R (ADR-0005 as built) + mapped view, POSIX rename", N.FILE_SHARE_READ, true, "posix"),
                ("no holder, MoveFileEx", 0u, false, "movefileex"),
            })
            {
                Log($"--- {name}");
                SafeFileHandle? h = null;
                EngineView? v = null;
                if (share != 0)
                {
                    var (hh, e) = N.Open(doc, N.GENERIC_READ, share);
                    if (hh is null) { Log($"cannot open for the holder: {N.Err(e)}"); continue; }
                    h = hh;
                    if (mapped) v = new EngineView(EngineView.DuplicateInProcess(h));
                }

                var temp = Path.Combine(dir, ".doc.pdf.mikepdf-" + Guid.NewGuid().ToString("N")[..8] + ".tmp");
                File.WriteAllBytes(temp, b);
                File.SetAttributes(temp, FileAttributes.Hidden);
                Log($"temp created: {Attrs(temp)}");
                int err;
                switch (how)
                {
                    case "posix":
                    {
                        var (r, e) = N.Open(temp, N.DELETE | N.GENERIC_READ, 7);
                        err = r is null ? e : N.RenameByHandle(r, doc, N.FILE_RENAME_FLAG_REPLACE_IF_EXISTS | N.FILE_RENAME_FLAG_POSIX_SEMANTICS, true);
                        r?.Dispose();
                        break;
                    }
                    case "replacefile":
                        err = N.ReplaceFileW(doc, temp, null, 0, IntPtr.Zero, IntPtr.Zero) ? 0 : System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                        break;
                    default:
                        err = N.MoveFileExW(temp, doc, N.MOVEFILE_REPLACE_EXISTING) ? 0 : System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                        break;
                }

                Log(err == 0 ? "replace: OK" : $"replace: FAILED {N.Err(err)}");
                v?.Dispose();
                h?.Dispose();
                if (err != 0) { try { File.Delete(temp); } catch { } continue; }
                var content = Matrix.Hash(File.ReadAllBytes(doc)) == Matrix.Hash(b) ? "new content" : "OLD/other content";
                Log($"after replace: {content}; attributes {Attrs(doc)}; folder: [{string.Join(", ", Siblings(dir))}]");
                var sync = WaitSynced(doc, 90);
                Log(sync >= 0 ? $"new doc.pdf became a placeholder after {sync:0.0}s" : "new doc.pdf did not become a placeholder in 90s");
                Log($"folder after sync: [{string.Join(", ", Siblings(dir))}]  (a conflict copy would be named like doc-<COMPUTERNAME>.pdf)");
                (a, b) = (b, a); // alternate contents so every round is a real change
            }

            // 2. Burst of saves (create temp, write, POSIX replace) with OneDrive watching: any transient failures?
            Log("--- burst: 40 saves, 150 ms apart, POSIX rename, no holder");
            var failures = new Dictionary<string, int>();
            for (var i = 0; i < 40; i++)
            {
                var temp = Path.Combine(dir, ".doc.pdf.mikepdf-burst" + i + ".tmp");
                File.WriteAllBytes(temp, i % 2 == 0 ? a : b);
                var (r, e) = N.Open(temp, N.DELETE | N.GENERIC_READ, 7);
                var err = r is null ? e : N.RenameByHandle(r, doc, N.FILE_RENAME_FLAG_REPLACE_IF_EXISTS | N.FILE_RENAME_FLAG_POSIX_SEMANTICS, true);
                r?.Dispose();
                if (err != 0) { var k = N.Err(err); failures[k] = failures.GetValueOrDefault(k) + 1; try { File.Delete(temp); } catch { } }
                Thread.Sleep(150);
            }

            Log(failures.Count == 0 ? "burst: 40 of 40 replaces succeeded" : "burst failures: " + string.Join("; ", failures.Select(kv => $"{kv.Key} x{kv.Value}")));
            Log($"folder after burst: [{string.Join(", ", Siblings(dir))}]");

            // 3. Online-only (dehydrated) file: replace it without ever opening it, then open it with the proposed flags and replace again.
            Log("--- online-only file");
            WaitSynced(doc, 60);
            var p = Process.Start(new ProcessStartInfo("attrib.exe", $"+U -P \"{doc}\"") { UseShellExecute = false, CreateNoWindow = true });
            p!.WaitForExit();
            Thread.Sleep(1500);
            Log($"after attrib +U -P: {Attrs(doc)}");
            var temp2 = Path.Combine(dir, ".doc.pdf.mikepdf-dehydrated.tmp");
            File.WriteAllBytes(temp2, b);
            var ok = N.ReplaceFileW(doc, temp2, null, 0, IntPtr.Zero, IntPtr.Zero);
            Log(ok ? $"ReplaceFileW over an online-only placeholder: OK; now {Attrs(doc)}" : $"ReplaceFileW over an online-only placeholder: FAILED {N.Err(System.Runtime.InteropServices.Marshal.GetLastWin32Error())}");
            Log($"folder: [{string.Join(", ", Siblings(dir))}]");
        }
        finally
        {
            Log("--- cleanup");
            Thread.Sleep(3000);
            foreach (var f in Directory.EnumerateFileSystemEntries(dir))
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            try { Directory.Delete(dir, true); Log("test folder deleted"); } catch (Exception ex) { Log("cleanup failed: " + ex.Message); }
            _log?.Dispose();
        }
    }
}
