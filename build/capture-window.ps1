<#
.SYNOPSIS
  Saves a PNG of a running window (by process name or id) from the screen. Used for release evidence (build/out/shots/).
.DESCRIPTION
  Brings the window to the front, then copies its screen rectangle. The window must be visible on an unlocked, interactive desktop.
  Nothing is sent anywhere. Output goes to build/out/shots/ unless -Path is given.
.EXAMPLE
  ./build/capture-window.ps1 -Name MikePDF -Label 01-empty
#>
param(
    [string] $Name = 'MikePDF',
    [int] $ProcessId = 0,
    [string] $Label = 'window',
    [string] $Path
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class Win32Capture {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
}
'@
[void][Win32Capture]::SetProcessDPIAware()
$process = if ($ProcessId -gt 0) { Get-Process -Id $ProcessId } else { Get-Process -Name $Name | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1 }
if (-not $process -or $process.MainWindowHandle -eq 0) { throw "No window found for $Name" }
[void][Win32Capture]::ShowWindow($process.MainWindowHandle, 9)
[void][Win32Capture]::SetForegroundWindow($process.MainWindowHandle)
Start-Sleep -Milliseconds 700
$rect = New-Object Win32Capture+RECT
# DWMWA_EXTENDED_FRAME_BOUNDS = 9: the visible frame without the invisible resize border.
if ([Win32Capture]::DwmGetWindowAttribute($process.MainWindowHandle, 9, [ref]$rect, 16) -ne 0) { [void][Win32Capture]::GetWindowRect($process.MainWindowHandle, [ref]$rect) }
$w = $rect.Right - $rect.Left
$h = $rect.Bottom - $rect.Top
$bitmap = New-Object System.Drawing.Bitmap $w, $h
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size $w, $h))
if (-not $Path) {
    $dir = Join-Path $PSScriptRoot 'out/shots'
    New-Item -ItemType Directory -Force $dir | Out-Null
    $Path = Join-Path $dir "$Label.png"
}
$bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
$graphics.Dispose(); $bitmap.Dispose()
Write-Output $Path
