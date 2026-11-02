param(
    [Parameter(Mandatory)][int]$ProcessId,
    [Parameter(Mandatory)][string]$Out
)
# Captures a top-level window of a process with PrintWindow(PW_RENDERFULLCONTENT) so overlapping windows do not matter.
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices; using System.Text;
public static class W32 {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
}
"@
$found = $null
[W32]::EnumWindows({ param($h,$l)
    $pid2 = 0; [W32]::GetWindowThreadProcessId($h, [ref]$pid2) | Out-Null
    if ($pid2 -eq $ProcessId -and [W32]::IsWindowVisible($h)) {
        $cn = New-Object System.Text.StringBuilder 256; [W32]::GetClassName($h,$cn,256) | Out-Null
        if ($cn.ToString() -eq 'WinUIDesktopWin32WindowClass') { $script:found = $h; return $false }
    }
    return $true }, [IntPtr]::Zero) | Out-Null
if (-not $found) { Write-Error "no WinUI window found for pid $ProcessId"; exit 2 }
$r = New-Object W32+RECT; [W32]::GetWindowRect($found, [ref]$r) | Out-Null
$w = $r.R - $r.L; $hgt = $r.B - $r.T
$bmp = New-Object System.Drawing.Bitmap $w, $hgt
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
$ok = [W32]::PrintWindow($found, $hdc, 2)
$g.ReleaseHdc($hdc); $g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
"PrintWindow ok=$ok size=${w}x${hgt} -> $Out"
