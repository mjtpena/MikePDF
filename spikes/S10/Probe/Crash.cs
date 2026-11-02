// S10 item 6: kill the saving process at each step of a simulated save, then run the recovery rule and look at what is left.
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace S10;

internal static class Crash
{
    private const string TempName = ".doc.pdf.mikepdf-0a1b2c3d.tmp";
    private const string OldName = ".doc.pdf.mikepdf-0a1b2c3d.old";

    public static readonly string[] Mechanisms =
    [
        "release-then-ReplaceFileW",
        "release-then-MoveFileEx",
        "release-then-RenameEx(POSIX)",
        "hold-view: swap(rename away, rename in)",
    ];

    public static readonly string[] StepNames =
    [
        "0 before anything",
        "1 after SaveIntent record",
        "2 temp created (empty)",
        "3 temp half written",
        "4 temp complete + flushed",
        "5 validated, SaveBegin record flushed",
        "6 engines released / target renamed away",
        "7 replace done, before cleanup",
        "8 SaveCommitted + cleanup done",
    ];

    public static void RunAll(string root, string? outFile)
    {
        var rows = new List<Dictionary<string, object?>>();
        var a = Matrix.Content(1);
        var b = Matrix.Content(2);
        var ha = Matrix.Hash(a);
        var hb = Matrix.Hash(b);
        foreach (var design in new[] { "literal", "intent" })
        {
            foreach (var mech in Mechanisms)
            {
                for (var step = 0; step <= 8; step++)
                {
                    var dir = Path.Combine(root, $"c{rows.Count:D3}");
                    Directory.CreateDirectory(dir);
                    File.WriteAllBytes(Path.Combine(dir, "doc.pdf"), a);
                    var psi = new ProcessStartInfo(Environment.ProcessPath!, $"savesim \"{dir}\" \"{mech}\" {step} {design}") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
                    using var p = Process.Start(psi)!;
                    p.StandardOutput.ReadToEnd();
                    p.WaitForExit();
                    var killed = p.ExitCode == 99;

                    var stateBefore = Describe(dir, ha, hb);
                    var action = Recover(dir, hb);
                    var stateAfter = Describe(dir, ha, hb);
                    var row = new Dictionary<string, object?>
                    {
                        ["design"] = design, ["mechanism"] = mech, ["killStep"] = StepNames[step], ["killed"] = killed,
                        ["afterCrash"] = stateBefore, ["recovery"] = action, ["afterRecovery"] = stateAfter,
                        ["verdict"] = Verdict(dir, ha, hb),
                    };
                    rows.Add(row);
                    Console.WriteLine($"{design,-8} {mech,-42} {StepNames[step],-44} crash:[{stateBefore}] recover:{action} -> [{stateAfter}] {row["verdict"]}");
                    try { Directory.Delete(dir, true); } catch { }
                }
            }
        }

        if (outFile != null) File.AppendAllLines(outFile, rows.Select(r => JsonSerializer.Serialize(r)));
    }

    private static string Describe(string dir, string ha, string hb)
    {
        var t = Path.Combine(dir, "doc.pdf");
        var target = !File.Exists(t) ? "target MISSING" : Matrix.Hash(File.ReadAllBytes(t)) is var h && h == ha ? "target=A(old)" : h == hb ? "target=B(new)" : "target=TORN/OTHER";
        var left = Directory.GetFileSystemEntries(dir).Select(Path.GetFileName).Where(f => f is not ("doc.pdf" or "journal.jsonl")).Select(f => f == TempName ? "tmp" : f == OldName ? "old" : f).ToList();
        return target + (left.Count > 0 ? " +" + string.Join("+", left) : "");
    }

    private static string Verdict(string dir, string ha, string hb)
    {
        var t = Path.Combine(dir, "doc.pdf");
        if (!File.Exists(t)) return "BAD: target missing";
        var h = Matrix.Hash(File.ReadAllBytes(t));
        if (h != ha && h != hb) return "BAD: target torn";
        var stray = Directory.GetFileSystemEntries(dir).Select(Path.GetFileName).Where(f => f is not ("doc.pdf" or "journal.jsonl")).ToList();
        return stray.Count > 0 ? "LEAK: " + string.Join(",", stray) : "clean";
    }

    // ---- journal

    private static void Append(string dir, object record)
    {
        var line = JsonSerializer.Serialize(record) + "\n";
        using var f = N.CreateFileW(Path.Combine(dir, "journal.jsonl"), 0x4 /* FILE_APPEND_DATA */, 1, IntPtr.Zero, N.OPEN_ALWAYS, N.FILE_FLAG_WRITE_THROUGH, IntPtr.Zero);
        var bytes = Encoding.UTF8.GetBytes(line);
        using var fs = new FileStream(f, FileAccess.Write, 1, false);
        fs.Write(bytes);
        fs.Flush(true);
    }

    private static List<JsonElement> ReadJournal(string dir)
    {
        var path = Path.Combine(dir, "journal.jsonl");
        var list = new List<JsonElement>();
        if (!File.Exists(path)) return list;
        foreach (var line in File.ReadAllLines(path))
        {
            try { list.Add(JsonDocument.Parse(line).RootElement.Clone()); } catch { break; }
        }

        return list;
    }

    /// <summary>The start-up rule. It may delete only files a journal record names (spec 3.6 item 6), and finishes or rolls back a swap.</summary>
    private static string Recover(string dir, string hb)
    {
        var recs = ReadJournal(dir);
        var target = Path.Combine(dir, "doc.pdf");
        var temp = Path.Combine(dir, TempName);
        var old = Path.Combine(dir, OldName);
        var actions = new List<string>();
        var committed = recs.Any(r => r.GetProperty("type").GetString() == "SaveCommitted");
        var begin = recs.LastOrDefault(r => r.GetProperty("type").GetString() == "SaveBegin");
        var intent = recs.LastOrDefault(r => r.GetProperty("type").GetString() == "SaveIntent");
        var named = begin.ValueKind != JsonValueKind.Undefined || intent.ValueKind != JsonValueKind.Undefined;

        if (begin.ValueKind != JsonValueKind.Undefined && !committed)
        {
            var sha = begin.GetProperty("outputSha256").GetString();
            if (!File.Exists(target) && File.Exists(old))
            {
                if (File.Exists(temp) && Matrix.Hash(File.ReadAllBytes(temp)) == sha) { File.Move(temp, target); actions.Add("roll forward (temp->target)"); }
                else { File.Move(old, target); actions.Add("roll back (old->target)"); }
            }
            else if (File.Exists(target) && Matrix.Hash(File.ReadAllBytes(target)) == sha)
            {
                actions.Add("replace had completed");
            }
        }

        if (named || committed)
        {
            if (File.Exists(temp)) { File.Delete(temp); actions.Add("deleted recorded tmp"); }
            if (File.Exists(old) && File.Exists(target)) { File.Delete(old); actions.Add("deleted recorded old"); }
            if (File.Exists(Path.Combine(dir, "journal.jsonl"))) File.Delete(Path.Combine(dir, "journal.jsonl"));
        }
        else if (File.Exists(temp)) actions.Add("tmp NOT recorded: left (rule: never delete unrecorded files)");

        return actions.Count == 0 ? "none" : string.Join(", ", actions);
    }

    // ---- the child: a save that dies at step k

    public static int SaveSimMain(string dir, string mech, int killStep, string design)
    {
        void K(int step)
        {
            if (step == killStep) N.TerminateProcess(N.GetCurrentProcess(), 99);
        }

        var target = Path.Combine(dir, "doc.pdf");
        var temp = Path.Combine(dir, TempName);
        var old = Path.Combine(dir, OldName);
        var b = Matrix.Content(2);

        // The document is open the way ADR-0005 holds it, plus the engine's view.
        var (h, e) = N.Open(target, N.GENERIC_READ, mech.StartsWith("hold") ? N.FILE_SHARE_READ | N.FILE_SHARE_DELETE : N.FILE_SHARE_READ);
        SafeFileHandle? broker = h;
        EngineView? view = new(EngineView.DuplicateInProcess(broker!));

        K(0);
        if (design == "intent") Append(dir, new { type = "SaveIntent", temp = TempName, old = OldName });
        K(1);
        var (t, te) = N.Open(temp, N.GENERIC_WRITE, 0, N.CREATE_NEW, N.FILE_ATTRIBUTE_NORMAL | 2 /* hidden */);
        if (t is null) return 3;
        K(2);
        using (t)
        {
            RandomAccess.Write(t, b.AsSpan(0, b.Length / 2), 0);
            K(3);
            RandomAccess.Write(t, b.AsSpan(b.Length / 2), b.Length / 2);
            N.FlushFileBuffers(t);
        }

        K(4);
        var sha = Matrix.Hash(File.ReadAllBytes(temp));
        Append(dir, new { type = "SaveBegin", temp = TempName, old = OldName, outputSha256 = sha, outputLength = b.Length });
        K(5);

        switch (mech)
        {
            case "release-then-ReplaceFileW":
                view.Dispose(); view = null; broker.Dispose(); broker = null;
                K(6);
                if (!N.ReplaceFileW(target, temp, null, 0, IntPtr.Zero, IntPtr.Zero)) return 4;
                break;
            case "release-then-MoveFileEx":
                view.Dispose(); view = null; broker.Dispose(); broker = null;
                K(6);
                if (!N.MoveFileExW(temp, target, N.MOVEFILE_REPLACE_EXISTING | N.MOVEFILE_WRITE_THROUGH)) return 4;
                break;
            case "release-then-RenameEx(POSIX)":
            {
                view.Dispose(); view = null; broker.Dispose(); broker = null;
                K(6);
                var (r, _) = N.Open(temp, N.DELETE | N.GENERIC_READ, 7);
                using (r!)
                {
                    if (N.RenameByHandle(r!, target, N.FILE_RENAME_FLAG_REPLACE_IF_EXISTS | N.FILE_RENAME_FLAG_POSIX_SEMANTICS, true) != 0) return 4;
                }

                break;
            }
            default: // hold-view swap: the engine keeps the old file mapped under its new name
                if (!N.MoveFileExW(target, old, 0)) return 4;
                K(6);
                if (!N.MoveFileExW(temp, target, 0)) return 4;
                break;
        }

        K(7);
        view?.Dispose(); broker?.Dispose();
        if (File.Exists(old)) File.Delete(old);
        Append(dir, new { type = "SaveCommitted" });
        K(8);
        return 0;
    }

    // ---- kill-during-replace stress: the process is killed at a random moment while it replaces in a loop

    public static void Stress(string root, int rounds, string? outFile)
    {
        var a = Matrix.Content(1);
        var b = Matrix.Content(2);
        var ha = Matrix.Hash(a);
        var hb = Matrix.Hash(b);
        var rows = new List<Dictionary<string, object?>>();
        var rnd = new Random(12345);
        var only = Environment.GetEnvironmentVariable("S10_STRESS_MECH");
        foreach (var mech in new[] { "ReplaceFileW", "MoveFileEx", "RenameEx(POSIX)", "swap(rename away, rename in)" }.Where(m => only is null or "" || m == only))
        {
            int missing = 0, torn = 0, okA = 0, okB = 0, strayTmp = 0, strayOld = 0;
            long loops = 0;
            for (var r = 0; r < rounds; r++)
            {
                var dir = Path.Combine(root, "s" + r);
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, "doc.pdf"), a);
                var psi = new ProcessStartInfo(Environment.ProcessPath!, $"stress-child \"{dir}\" \"{mech}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
                using var p = Process.Start(psi)!;
                p.StandardOutput.ReadLine(); // "LOOPING"
                Thread.Sleep(rnd.Next(5, 120));
                p.Kill();
                p.WaitForExit();
                var t = Path.Combine(dir, "doc.pdf");
                if (!File.Exists(t))
                {
                    missing++;
                    if (missing <= 3) Console.WriteLine($"  [{mech}] target missing; directory held: {string.Join(", ", Directory.GetFileSystemEntries(dir).Select(f => Path.GetFileName(f) + " (" + new FileInfo(f).Length + " bytes, " + (Matrix.Hash(File.ReadAllBytes(f)) is var hh && hh == ha ? "old content" : hh == hb ? "new content" : "partial/other") + ")"))}");
                }
                else
                {
                    var h = Matrix.Hash(File.ReadAllBytes(t));
                    if (h == ha) okA++; else if (h == hb) okB++; else torn++;
                }

                strayTmp += Directory.GetFiles(dir, ".doc.pdf.mikepdf-*.tmp").Length;
                strayOld += Directory.GetFiles(dir, "*.old").Length;
                try { Directory.Delete(dir, true); } catch { }
            }

            var row = new Dictionary<string, object?> { ["mechanism"] = mech, ["rounds"] = rounds, ["targetA"] = okA, ["targetB"] = okB, ["targetMissing"] = missing, ["targetTorn"] = torn, ["strayTmpFiles"] = strayTmp, ["strayOldFiles"] = strayOld };
            rows.Add(row);
            Console.WriteLine(JsonSerializer.Serialize(row));
        }

        if (outFile != null) File.AppendAllLines(outFile, rows.Select(r => JsonSerializer.Serialize(r)));
    }

    public static void StressChild(string dir, string mech)
    {
        var target = Path.Combine(dir, "doc.pdf");
        var temp = Path.Combine(dir, TempName);
        var old = Path.Combine(dir, OldName);
        var a = Matrix.Content(1);
        var b = Matrix.Content(2);
        Console.Out.WriteLine("LOOPING");
        Console.Out.Flush();
        for (long i = 0; ; i++)
        {
            File.WriteAllBytes(temp, (i & 1) == 0 ? b : a);
            switch (mech)
            {
                case "ReplaceFileW":
                    N.ReplaceFileW(target, temp, null, 0, IntPtr.Zero, IntPtr.Zero);
                    break;
                case "MoveFileEx":
                    N.MoveFileExW(temp, target, N.MOVEFILE_REPLACE_EXISTING);
                    break;
                case "RenameEx(POSIX)":
                {
                    var (r, _) = N.Open(temp, N.DELETE | N.GENERIC_READ, 7);
                    using (r!) N.RenameByHandle(r!, target, N.FILE_RENAME_FLAG_REPLACE_IF_EXISTS | N.FILE_RENAME_FLAG_POSIX_SEMANTICS, true);
                    break;
                }
                default:
                    N.MoveFileExW(target, old, N.MOVEFILE_REPLACE_EXISTING);
                    N.MoveFileExW(temp, target, 0);
                    break;
            }
        }
    }
}
