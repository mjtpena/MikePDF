// S10 with the REAL broker and the REAL sandboxed engine (AppContainer, PDFium mapped view): can the document be replaced while the engine has it open?
// Runs the broker exactly as built (FileShare.Read) and with the proposed FileShare.Read | FileShare.Delete (one patched line, see the csproj).
using System.Diagnostics;
using MikePDF.Broker;
using MikePDF.Protocol;

namespace S10
{
    public static class Patch
    {
        public static FileShare Share = FileShare.Read;
    }

    internal static class Program
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "MikePDF.slnx"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
        }

        public static async Task<int> Main(string[] args)
        {
            var repo = RepoRoot();
            var built = Path.Combine(repo, "build", "out", "cmake-x64", "bin", "Release");
            var deploy = Path.Combine(repo, "build", "out", "s10-engine", Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(deploy);
            var profile = AppContainerProfile.CreateOrOpen("MikePDF.S10Spike", "MikePDF S10 spike", "Zero-capability AppContainer for the S10 replace spike");
            EngineDeployment.EnsureAppContainerAccess(deploy, profile);
            foreach (var f in new[] { "MikePDF.Engine.exe", "pdfium.dll" }) File.Copy(Path.Combine(built, f), Path.Combine(deploy, f));
            var fixtures = Path.Combine(repo, "tests", "MikePDF.Integration.Tests", "Fixtures");
            var work = Path.Combine(Path.GetTempPath(), "mikepdf-s10-real-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(work);
            var outFile = args.Length > 0 ? args[0] : null;
            var lines = new List<string>();
            void Log(string s) { Console.WriteLine(s); lines.Add(s); }
            try
            {
                Log($"engine: {Path.Combine(deploy, "MikePDF.Engine.exe")} (AppContainer profile MikePDF.S10Spike)");
                foreach (var share in new[] { FileShare.Read, FileShare.Read | FileShare.Delete })
                {
                    Patch.Share = share;
                    Log($"\n== broker opens the document with FileShare.{share}");
                    foreach (var mech in new[] { "ReplaceFileW", "ReplaceFileW+backup", "MoveFileEx(REPLACE_EXISTING)", "RenameEx(REPLACE|POSIX)", "swap (rename away, rename in)", "close the document first, then ReplaceFileW" })
                    {
                        var dir = Path.Combine(work, Guid.NewGuid().ToString("N")[..6]);
                        Directory.CreateDirectory(dir);
                        var doc = Path.Combine(dir, "doc.pdf");
                        var temp = Path.Combine(dir, ".doc.pdf.mikepdf-0a1b2c3d.tmp");
                        File.Copy(Path.Combine(fixtures, "hello_world_2_pages.pdf"), doc, true);   // 2 pages, 200 x 200
                        File.Copy(Path.Combine(fixtures, "rectangles.pdf"), temp, true);          // 1 page, 200 x 300
                        File.SetAttributes(temp, FileAttributes.Hidden);
                        await using var supervisor = await Start(deploy, profile, Log);
                        var opened = await supervisor.OpenDocumentAsync(doc);
                        if (opened.Status != Status.Ok) { Log($"{mech}: open failed {opened.Status} {opened.Detail}"); continue; }
                        var document = opened.Value!;
                        var before = (await document.GetInfoAsync()).Value!.PageCount;
                        var steps = new List<string>();
                        bool ok;
                        switch (mech)
                        {
                            case "ReplaceFileW": ok = Step(steps, "ReplaceFileW", N.ReplaceFileW(doc, temp, null, 0, IntPtr.Zero, IntPtr.Zero)); break;
                            case "ReplaceFileW+backup": ok = Step(steps, "ReplaceFileW", N.ReplaceFileW(doc, temp, Path.Combine(work, "doc.pdf.bak"), 0, IntPtr.Zero, IntPtr.Zero)); break;
                            case "MoveFileEx(REPLACE_EXISTING)": ok = Step(steps, "MoveFileEx", N.MoveFileExW(temp, doc, N.MOVEFILE_REPLACE_EXISTING)); break;
                            case "RenameEx(REPLACE|POSIX)":
                            {
                                var (h, e) = N.Open(temp, N.DELETE | N.GENERIC_READ, 7);
                                var r = h is null ? e : N.RenameByHandle(h, doc, N.FILE_RENAME_FLAG_REPLACE_IF_EXISTS | N.FILE_RENAME_FLAG_POSIX_SEMANTICS, true);
                                h?.Dispose();
                                ok = r == 0;
                                steps.Add(ok ? "SetFileInformationByHandle OK" : "SetFileInformationByHandle " + N.Err(r));
                                break;
                            }
                            case "swap (rename away, rename in)":
                                ok = Step(steps, "target->.old", N.MoveFileExW(doc, doc + ".old", 0)) && Step(steps, ".tmp->target", N.MoveFileExW(temp, doc, 0));
                                if (!ok && File.Exists(doc + ".old") && !File.Exists(doc)) File.Move(doc + ".old", doc);
                                break;
                            default:
                                await document.DisposeAsync();     // the engine unmaps and the broker closes its handle
                                ok = Step(steps, "ReplaceFileW after close", N.ReplaceFileW(doc, temp, null, 0, IntPtr.Zero, IntPtr.Zero));
                                break;
                        }

                        var seen = "document closed";
                        if (!mech.StartsWith("close", StringComparison.Ordinal))
                        {
                            var info = await document.GetInfoAsync();
                            var tile = await document.RenderTileAsync(new RenderTileArgs(0, 1f, 0, 0, 0, 200, 200), 1);
                            seen = $"old document still served by the engine: {info.Status}, pages {info.Value?.PageCount} (was {before}), render of page 1: {tile.Status}";
                            tile.Value?.Dispose();
                        }

                        // a brand-new open of the path sees the new bytes
                        // (a document-kind engine holds one document, so the new bytes are opened in a second engine, as the save pipeline's validation does)
                        await using var fresh = await Start(deploy, profile, Log);
                        var reopened = await fresh.OpenDocumentAsync(doc);
                        var nowPages = reopened.Status == Status.Ok ? (await reopened.Value!.GetInfoAsync()).Value!.PageCount : 0u;
                        Log($"{mech,-46} -> {(ok ? "REPLACED" : "FAILED  ")} [{string.Join("; ", steps)}] | {seen} | fresh open of the path: {nowPages} page(s) ({(nowPages == 1 ? "new file" : "old file")})");
                        if (reopened.Status == Status.Ok) await reopened.Value!.DisposeAsync();
                        if (!mech.StartsWith("close", StringComparison.Ordinal)) await document.DisposeAsync();
                        Log($"    folder afterwards: [{string.Join(", ", Directory.GetFileSystemEntries(dir).Select(x => Path.GetFileName(x) + ((File.GetAttributes(x) & FileAttributes.Hidden) != 0 ? " (hidden)" : "")))}]");
                    }
                }
            }
            finally
            {
                try { EngineDeployment.RevokeAppContainerAccess(deploy, profile.Sid); } catch { }
                try { Directory.Delete(deploy, true); } catch { }
                try { Directory.Delete(work, true); } catch { }
                try { profile.Delete(); } catch { }
                if (outFile != null) File.WriteAllLines(outFile, lines);
            }

            return 0;
        }

        private static async Task<EngineSupervisor> Start(string deploy, AppContainerProfile profile, Action<string> log)
        {
            for (var attempt = 1; ; attempt++)
            {
                try { return await EngineSupervisor.StartAsync(new EngineSupervisorOptions { EnginePath = Path.Combine(deploy, "MikePDF.Engine.exe"), Profile = profile, SlotCount = 4, SlotSize = 4 << 20 }); }
                catch (EngineStartException e) when (attempt < 4) { log($"    (engine start failed, attempt {attempt}: {e.Message.Split((char)10)[0]}; retrying)"); }
            }
        }

        private static bool Step(List<string> steps, string name, bool ok)
        {
            steps.Add(ok ? name + " OK" : name + " " + N.Err(System.Runtime.InteropServices.Marshal.GetLastWin32Error()));
            return ok;
        }
    }
}
