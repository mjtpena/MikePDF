// S10 item 3: what survives a replace (ACL, Zone.Identifier and other streams, attributes, times, file ID, hard links, symlinks).
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace S10;

internal static unsafe class Meta
{
    private const string Zone = "[ZoneTransfer]\r\nZoneId=3\r\nReferrerUrl=https://example.test/page\r\nHostUrl=https://example.test/file.pdf\r\n";
    private static readonly DateTime OldCreation = new(2001, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime OldWrite = new(2020, 6, 1, 8, 30, 0, DateTimeKind.Utc);

    public static readonly string[] Mechanisms =
    [
        "ReplaceFileW",
        "ReplaceFileW (IGNORE_MERGE_ERRORS|IGNORE_ACL_ERRORS)",
        "MoveFileEx(REPLACE_EXISTING)",
        "RenameEx(REPLACE|POSIX)",
        "swap: rename target away, rename temp in",
        "ReplaceFileW + explicit metadata copy first",
        "RenameEx(REPLACE|POSIX) + explicit metadata copy first",
        "RenameEx(REPLACE|POSIX) + metadata copy, symlink resolved",
    ];

    public static void Run(string root, string label, string? outFile)
    {
        var rows = new List<Dictionary<string, object?>>();
        var subset = Environment.GetEnvironmentVariable("S10_SUBSET") == "1";
        foreach (var variant in subset ? new[] { "normal", "hardlink" } : new[] { "normal", "readonly-attr", "compressed", "symlink", "hardlink" })
        {
            foreach (var mech in Mechanisms)
            {
                var dir = Path.Combine(root, $"meta-{variant}-{Math.Abs(mech.GetHashCode()) % 100000}");
                Directory.CreateDirectory(dir);
                var row = Scenario(dir, variant, mech);
                row["root"] = label;
                row["variant"] = variant;
                row["mechanism"] = mech;
                rows.Add(row);
                Console.WriteLine($"{variant,-14} {mech,-52} {JsonSerializer.Serialize(row.Where(kv => kv.Key is not ("root" or "variant" or "mechanism")).ToDictionary(kv => kv.Key, kv => kv.Value))}");
                try { ClearAttributes(dir); Directory.Delete(dir, true); } catch (Exception ex) { Console.WriteLine("  cleanup: " + ex.Message); }
            }
        }

        if (outFile != null) File.AppendAllLines(outFile, rows.Select(r => JsonSerializer.Serialize(r)));
    }

    private static void ClearAttributes(string dir)
    {
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
    }

    private static Dictionary<string, object?> Scenario(string dir, string variant, string mech)
    {
        var target = Path.Combine(dir, "doc.pdf");
        var realFile = target;
        var temp = Path.Combine(dir, ".doc.pdf.mikepdf-0a1b2c3d.tmp");
        var link = Path.Combine(dir, "doc.hardlink.pdf");
        var a = Matrix.Content(1);
        var b = Matrix.Content(2);
        if (variant == "symlink")
        {
            Directory.CreateDirectory(Path.Combine(dir, "real"));
            realFile = Path.Combine(dir, "real", "doc.pdf");
            File.WriteAllBytes(realFile, a);
            File.CreateSymbolicLink(target, realFile);
        }
        else
        {
            File.WriteAllBytes(target, a);
        }

        // Give the original distinctive metadata.
        var me = WindowsIdentity.GetCurrent().User!;
        var fi = new FileInfo(realFile);
        var sec = new FileSecurity();
        sec.SetAccessRuleProtection(true, false);
        sec.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.FullControl, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        fi.SetAccessControl(sec);
        WriteStream(realFile, "Zone.Identifier", Zone);
        WriteStream(realFile, "mikepdf.custom", "custom stream payload");
        var attrs = FileAttributes.Hidden | FileAttributes.Archive | FileAttributes.NotContentIndexed;
        if (variant == "readonly-attr") attrs |= FileAttributes.ReadOnly;
        File.SetAttributes(realFile, attrs);
        if (variant == "compressed") SetCompression(realFile);
        if (variant == "hardlink") N.CreateHardLinkW(link, realFile, IntPtr.Zero);
        SetTimes(realFile, OldCreation, OldWrite);

        File.WriteAllBytes(temp, b);
        File.SetAttributes(temp, FileAttributes.Hidden);

        var before = Snapshot(variant == "symlink" ? realFile : target);
        var beforeLinkInfo = variant == "symlink" ? Info(target, reparse: true) : null;
        var tempSnap = Snapshot(temp);

        var ok = false;
        var err = "";
        try
        {
            if (mech.Contains("metadata copy")) CopyMetadata(realFile, temp);
            ok = Apply(mech, mech.Contains("symlink resolved") ? realFile : target, temp, dir, out err);
        }
        catch (Exception ex) { err = ex.Message; }

        var after = Snapshot(mech.Contains("symlink resolved") ? realFile : target);   // a symlink path would give the link's own ACL and no streams
        var d = new Dictionary<string, object?> { ["ok"] = ok, ["error"] = err };
        if (!ok) return d;
        d["content"] = Matrix.Hash(File.ReadAllBytes(target)) == Matrix.Hash(b) ? "B(new)" : "other";
        d["dacl"] = after.Sddl == before.Sddl ? "kept" : after.Sddl == tempSnap.Sddl ? "TEMP's (original lost)" : "changed";
        if (mech.Contains("metadata copy") && variant == "normal") { d["sddlBefore"] = before.Sddl; d["sddlAfter"] = after.Sddl; d["sddlTemp"] = tempSnap.Sddl; }
        d["owner"] = after.Owner == before.Owner ? "same" : "changed";
        d["zone"] = after.Streams.GetValueOrDefault("Zone.Identifier") == Zone ? "kept" : after.Streams.ContainsKey("Zone.Identifier") ? "changed" : "LOST";
        d["customStream"] = after.Streams.ContainsKey("mikepdf.custom") ? "kept" : "LOST";
        d["attrs"] = $"{(before.Attrs & Mask):X}->{(after.Attrs & Mask):X}" + ((before.Attrs & Mask) == (after.Attrs & Mask) ? " kept" : " CHANGED");
        d["creation"] = Math.Abs((after.Creation - before.Creation).TotalSeconds) < 2 ? "kept (2001)" : Math.Abs((after.Creation - tempSnap.Creation).TotalSeconds) < 2 ? "TEMP's (new)" : "other";
        d["fileId"] = after.FileId == before.FileId ? "same" : after.FileId == tempSnap.FileId ? "TEMP's" : "new";
        d["links"] = $"{before.Links}->{after.Links}";
        if (variant == "hardlink")
            d["hardlinkName"] = Matrix.Hash(File.ReadAllBytes(link)) == Matrix.Hash(a) ? "still OLD content (link broken)" : "new content (link followed)";
        if (variant == "symlink")
        {
            var afterLink = Info(target, reparse: true);
            d["symlink"] = afterLink.IsReparse ? "still a symlink" : "REPLACED BY REGULAR FILE";
            d["realFile"] = Matrix.Hash(File.ReadAllBytes(realFile)) == Matrix.Hash(b) ? "real file updated" : "real file untouched";
        }

        return d;
    }

    private const FileAttributes Mask = FileAttributes.Hidden | FileAttributes.ReadOnly | FileAttributes.Archive | FileAttributes.NotContentIndexed | FileAttributes.Compressed | FileAttributes.System | FileAttributes.Temporary;

    private static bool Apply(string mech, string target, string temp, string dir, out string err)
    {
        err = "";
        bool ok;
        switch (mech)
        {
            case "ReplaceFileW":
                ok = N.ReplaceFileW(target, temp, null, 0, IntPtr.Zero, IntPtr.Zero);
                break;
            case "ReplaceFileW (IGNORE_MERGE_ERRORS|IGNORE_ACL_ERRORS)":
                ok = N.ReplaceFileW(target, temp, null, N.REPLACEFILE_IGNORE_MERGE_ERRORS | N.REPLACEFILE_IGNORE_ACL_ERRORS, IntPtr.Zero, IntPtr.Zero);
                break;
            case "ReplaceFileW + explicit metadata copy first":
                ok = N.ReplaceFileW(target, temp, null, 0, IntPtr.Zero, IntPtr.Zero);
                break;
            case "MoveFileEx(REPLACE_EXISTING)":
                ok = N.MoveFileExW(temp, target, N.MOVEFILE_REPLACE_EXISTING);
                break;
            case "RenameEx(REPLACE|POSIX)":
            case "RenameEx(REPLACE|POSIX) + explicit metadata copy first":
            case "RenameEx(REPLACE|POSIX) + metadata copy, symlink resolved":
            {
                var (h, e) = N.Open(temp, N.DELETE | N.GENERIC_READ, 7);
                if (h is null) { err = N.Err(e); return false; }
                using (h)
                {
                    var r = N.RenameByHandle(h, target, N.FILE_RENAME_FLAG_REPLACE_IF_EXISTS | N.FILE_RENAME_FLAG_POSIX_SEMANTICS, true);
                    if (r != 0) err = N.Err(r);
                    return r == 0;
                }
            }
            case "swap: rename target away, rename temp in":
            {
                var old = Path.Combine(dir, "doc.pdf.old");
                ok = N.MoveFileExW(target, old, 0) && N.MoveFileExW(temp, target, 0);
                if (ok) try { File.Delete(old); } catch { }
                break;
            }
            default:
                throw new ArgumentException(mech);
        }

        if (!ok) err = N.Err(Marshal.GetLastWin32Error());
        return ok;
    }

    // ---- snapshot helpers

    private sealed record Snap(string Sddl, string Owner, Dictionary<string, string> Streams, FileAttributes Attrs, DateTime Creation, DateTime Write, string FileId, uint Links);

    private static Snap Snapshot(string path)
    {
        var fi = new FileInfo(path);
        var sec = fi.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        var inf = Info(path, reparse: false);
        return new Snap(sec.GetSecurityDescriptorSddlForm(AccessControlSections.Access), sec.GetOwner(typeof(SecurityIdentifier))?.Value ?? "?", ReadStreams(path), fi.Attributes, fi.CreationTimeUtc, fi.LastWriteTimeUtc, inf.FileId, inf.Links);
    }

    private sealed record FInfo(string FileId, uint Links, bool IsReparse);

    private static FInfo Info(string path, bool reparse)
    {
        var (h, e) = N.Open(path, N.FILE_READ_ATTRIBUTES, 7, N.OPEN_EXISTING, N.FILE_FLAG_BACKUP_SEMANTICS | (reparse ? N.FILE_FLAG_OPEN_REPARSE_POINT : 0));
        if (h is null) return new FInfo("open failed " + e, 0, false);
        using (h)
        {
            var idBuf = stackalloc byte[24]; // FILE_ID_INFO: VolumeSerialNumber (8) + FileId (16)
            string id = "?";
            if (N.GetFileInformationByHandleEx(h, N.FileIdInfo, idBuf, 24))
                id = Convert.ToHexString(new ReadOnlySpan<byte>(idBuf, 24));
            N.GetFileInformationByHandle(h, out var info);
            return new FInfo(id, info.NumberOfLinks, (info.FileAttributes & 0x400) != 0);
        }
    }

    public static Dictionary<string, string> ReadStreams(string path)
    {
        var result = new Dictionary<string, string>();
        var h = N.FindFirstStreamW(path, 0, out var data, 0);
        if (h == new IntPtr(-1)) return result;
        try
        {
            do
            {
                var name = data.StreamName; // ":Zone.Identifier:$DATA" or "::$DATA"
                if (name == "::$DATA") continue;
                var bare = name.Substring(1, name.LastIndexOf(':') - 1);
                result[bare] = ReadStream(path, bare);
            }
            while (N.FindNextStreamW(h, out data));
        }
        finally { N.FindClose(h); }
        return result;
    }

    private static string ReadStream(string path, string stream)
    {
        using var f = File.OpenHandle(path + ":" + stream, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var len = RandomAccess.GetLength(f);
        var buf = new byte[len];
        RandomAccess.Read(f, buf, 0);
        return Encoding.UTF8.GetString(buf);
    }

    private static void WriteStream(string path, string stream, string text)
    {
        using var f = File.OpenHandle(path + ":" + stream, FileMode.Create, FileAccess.Write, FileShare.None);
        RandomAccess.Write(f, Encoding.UTF8.GetBytes(text), 0);
    }

    private static void SetTimes(string path, DateTime creationUtc, DateTime writeUtc)
    {
        var (h, e) = N.Open(path, N.FILE_WRITE_ATTRIBUTES, 7);
        if (h is null) throw new IOException("SetTimes open " + N.Err(e));
        using (h)
        {
            var c = creationUtc.ToFileTimeUtc();
            var w = writeUtc.ToFileTimeUtc();
            N.SetFileTime(h, &c, null, &w);
        }
    }

    private static void SetCompression(string path)
    {
        var (h, e) = N.Open(path, N.GENERIC_READ | N.GENERIC_WRITE, 7);
        if (h is null) throw new IOException("compress open " + N.Err(e));
        using (h)
        {
            ushort fmt = 1; // COMPRESSION_FORMAT_DEFAULT
            N.DeviceIoControl(h, 0x9C040, &fmt, 2, null, 0, out _, IntPtr.Zero);
        }
    }

    /// <summary>The explicit copy a replace implementation would do when the mechanism does not carry metadata across: DACL, owner if possible, streams, attributes, times.</summary>
    private static void CopyMetadata(string from, string to)
    {
        // Note: SetAccessControl(GetAccessControl(...)) writes NOTHING, because .NET persists only sections marked modified (first attempt in this spike: DACL silently not copied).
        // Loading the binary descriptor into a fresh FileSecurity marks the DACL modified.
        var srcSec = new FileInfo(from).GetAccessControl(AccessControlSections.Access);
        var sec = new FileSecurity();
        sec.SetSecurityDescriptorBinaryForm(srcSec.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
        new FileInfo(to).SetAccessControl(sec);
        foreach (var (name, text) in ReadStreams(from)) WriteStream(to, name, text);
        var fi = new FileInfo(from);
        File.SetAttributes(to, fi.Attributes & ~(FileAttributes.Compressed | FileAttributes.ReparsePoint | FileAttributes.SparseFile));
        SetTimes(to, fi.CreationTimeUtc, fi.LastWriteTimeUtc);
    }
}
