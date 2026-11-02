// S10 item 2: with the broker's handle open under each sharing mode (and an engine view), what can ANOTHER PROCESS do to the file?
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace S10;

internal static unsafe class Attack
{
    public static readonly string[] Ops = ["open-for-write", "open-for-append", "touch-mtime (attribute-only open)", "rename", "delete", "posix-delete", "ReplaceFileW over it", "open-for-read"];

    public static void Run(string root, string? outFile)
    {
        var rows = new List<Dictionary<string, object?>>();
        var a = Matrix.Content(1);
        var ha = Matrix.Hash(a);
        (string Name, uint Share)[] shares =
        [
            ("R (ADR-0005 as built)", N.FILE_SHARE_READ),
            ("R|D (proposed)", N.FILE_SHARE_READ | N.FILE_SHARE_DELETE),
            ("R|W|D", N.FILE_SHARE_READ | N.FILE_SHARE_WRITE | N.FILE_SHARE_DELETE),
            ("none", 0),
        ];
        var n = 0;
        foreach (var (name, share) in shares)
        {
            foreach (var withView in new[] { false, true })
            {
                foreach (var op in Ops)
                {
                    var dir = Path.Combine(root, $"a{n++:D3}");
                    Directory.CreateDirectory(dir);
                    var target = Path.Combine(dir, "doc.pdf");
                    var other = Path.Combine(dir, "other.pdf");
                    File.WriteAllBytes(target, a);
                    File.WriteAllBytes(other, Matrix.Content(9));
                    var (h, e) = N.Open(target, N.GENERIC_READ, share);
                    if (h is null) { Console.WriteLine("holder open failed " + N.Err(e)); continue; }
                    using var broker = h;
                    EngineView? view = withView ? new EngineView(EngineView.DuplicateInProcess(broker)) : null;
                    N.GetFileInformationByHandle(broker, out var bi);

                    var psi = new ProcessStartInfo(Environment.ProcessPath!, $"attacker \"{target}\" \"{op}\" \"{other}\"") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                    using var p = Process.Start(psi)!;
                    var outp = p.StandardOutput.ReadToEnd().Trim();
                    p.WaitForExit();

                    var buf = new byte[Matrix.FileSize];
                    var read = RandomAccess.Read(broker, buf, 0);
                    var brokerSees = read == buf.Length && Matrix.Hash(buf) == ha ? "A intact" : "CHANGED";
                    string pathNow;
                    try { pathNow = !File.Exists(target) ? "path missing" : Matrix.Hash(File.ReadAllBytes(target)) == ha ? "path = A" : "path = other content"; }
                    catch (IOException) { pathNow = "path unreadable by the broker itself (share=none blocks readers)"; }
                    string sameFile = "-";
                    var (ph, _) = N.Open(target, N.FILE_READ_ATTRIBUTES, 7);
                    if (ph != null)
                    {
                        using (ph)
                        {
                            N.GetFileInformationByHandle(ph, out var pi);
                            sameFile = pi.FileIndexHigh == bi.FileIndexHigh && pi.FileIndexLow == bi.FileIndexLow ? "same file id" : "DIFFERENT file at path";
                        }
                    }

                    var row = new Dictionary<string, object?> { ["brokerShare"] = name, ["engineView"] = withView, ["op"] = op, ["result"] = outp, ["brokerHandleSees"] = brokerSees, ["pathNow"] = pathNow, ["pathIdentity"] = sameFile };
                    rows.Add(row);
                    Console.WriteLine($"{name,-24} view={withView,-5} {op,-36} -> {outp,-70} | broker:{brokerSees} | {pathNow} | {sameFile}");
                    view?.Dispose();
                    broker.Dispose();
                    try { Directory.Delete(dir, true); } catch { }
                }
            }
        }

        if (outFile != null) File.AppendAllLines(outFile, rows.Select(r => JsonSerializer.Serialize(r)));
    }

    /// <summary>The other process.</summary>
    public static void AttackerMain(string target, string op, string other)
    {
        string R(bool ok) => ok ? "SUCCEEDED" : "denied: " + N.Err(Marshal.GetLastWin32Error());
        switch (op)
        {
            case "open-for-write":
            {
                var (h, e) = N.Open(target, N.GENERIC_WRITE, 7);
                Console.WriteLine(h != null ? "SUCCEEDED" : "denied: " + N.Err(e));
                h?.Dispose();
                break;
            }
            case "open-for-append":
            {
                var (h, e) = N.Open(target, 0x4 /* FILE_APPEND_DATA */, 7);
                Console.WriteLine(h != null ? "SUCCEEDED" : "denied: " + N.Err(e));
                h?.Dispose();
                break;
            }
            case "touch-mtime (attribute-only open)":
            {
                var (h, e) = N.Open(target, N.FILE_WRITE_ATTRIBUTES, 7);
                if (h is null) { Console.WriteLine("denied: " + N.Err(e)); break; }
                using (h)
                {
                    var w = DateTime.UtcNow.AddDays(-1).ToFileTimeUtc();
                    Console.WriteLine(R(N.SetFileTime(h, null, null, &w)));
                }

                break;
            }
            case "rename":
                Console.WriteLine(R(N.MoveFileExW(target, target + ".renamed", 0)));
                break;
            case "delete":
                Console.WriteLine(R(N.DeleteFileW(target)));
                break;
            case "posix-delete":
            {
                var (h, e) = N.Open(target, N.DELETE | N.FILE_READ_ATTRIBUTES, 7);
                if (h is null) { Console.WriteLine("open for delete denied: " + N.Err(e)); break; }
                using (h)
                {
                    var r = N.DisposeByHandle(h, N.FILE_DISPOSITION_FLAG_DELETE | N.FILE_DISPOSITION_FLAG_POSIX_SEMANTICS);
                    Console.WriteLine(r == 0 ? "SUCCEEDED" : "denied: " + N.Err(r));
                }

                break;
            }
            case "ReplaceFileW over it":
                Console.WriteLine(R(N.ReplaceFileW(target, other, null, 0, IntPtr.Zero, IntPtr.Zero)));
                break;
            case "open-for-read":
            {
                var (h, e) = N.Open(target, N.GENERIC_READ, 7);
                Console.WriteLine(h != null ? "SUCCEEDED" : "denied: " + N.Err(e));
                h?.Dispose();
                break;
            }
        }
    }
}
