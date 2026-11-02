// S10: what flags does .NET's File.OpenHandle(..., FileShare.X) actually pass? Observed from outside (another handle's attempts), not read from documentation.
using Microsoft.Win32.SafeHandles;

namespace S10;

internal static class NetShare
{
    public static void Run(string root)
    {
        Directory.CreateDirectory(root);
        foreach (var share in new[] { FileShare.Read, FileShare.Read | FileShare.Delete, FileShare.ReadWrite | FileShare.Delete, FileShare.None })
        {
            var path = Path.Combine(root, "net-" + (int)share + ".pdf");
            File.WriteAllBytes(path, Matrix.Content(1));
            using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, share);
            var (w, ew) = N.Open(path, N.GENERIC_WRITE, 7);
            w?.Dispose();
            var rename = N.MoveFileExW(path, path + ".x", 0);
            var er = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            if (rename) N.MoveFileExW(path + ".x", path, 0);
            var (r, er2) = N.Open(path, N.GENERIC_READ, 7);
            r?.Dispose();
            Console.WriteLine($"File.OpenHandle FileShare.{share,-22} -> other process: write-open {(w != null ? "ALLOWED" : "denied " + ew)}, rename {(rename ? "ALLOWED" : "denied " + er)}, read-open {(r != null ? "ALLOWED" : "denied " + er2)}");
            h.Dispose();
            File.Delete(path);
        }
    }
}
