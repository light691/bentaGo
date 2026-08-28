# BG-001 live-lane harness (coordinator-owned verification tool)
# Control path for WPF lanes: launches the app with an isolated data root,
# waits for the Ready proxy (main window handle), captures the screen.
# Usage:
#   .\bg001-live.ps1 -Exe <path> -OutDir <dir> -Lane <name> -DataRoot <dir> [-Width 1366 -Height 768]
# Environment: PowerShell 5.1. Screenshots: PNG via System.Drawing.
param(
  [Parameter(Mandatory = $true)][string]$Exe,
  [Parameter(Mandatory = $true)][string]$OutDir,
  [Parameter(Mandatory = $true)][string]$Lane,
  [string]$DataRoot = "",
  [int]$Width = 0,
  [int]$Height = 0,
  [string]$DotnetRoot = "C:\Users\nicky\.dotnet",
  [string]$AutoLogin = "",
  [switch]$WindowShot,
  [switch]$NoLaunch
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$shot = Join-Path $OutDir ("bg-001-" + $Lane + ".png")

function Capture([string]$path) {
  $b = New-Object System.Drawing.Bitmap([System.Windows.Forms.SystemInformation]::VirtualScreen.Width, [System.Windows.Forms.SystemInformation]::VirtualScreen.Height)
  $g = [System.Drawing.Graphics]::FromImage($b)
  $g.CopyFromScreen(0, 0, 0, 0, $b.Size)
  $b.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $b.Dispose()
}

function CaptureWindow([IntPtr]$hwnd, [string]$path) {
  Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class BgWin32Capture {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
}
"@
  $rect = New-Object BgWin32Capture+RECT
  [BgWin32Capture]::GetWindowRect($hwnd, [ref]$rect) | Out-Null
  $w = $rect.Right - $rect.Left
  $h = $rect.Bottom - $rect.Top
  if ($w -le 0 -or $h -le 0) { Write-Output ("CAPTURE: window rect invalid " + $w + "x" + $h); return }
  $b = New-Object System.Drawing.Bitmap($w, $h)
  $g = [System.Drawing.Graphics]::FromImage($b)
  $hdc = $g.GetHdc()
  $ok = [BgWin32Capture]::PrintWindow($hwnd, $hdc, 2)
  $g.ReleaseHdc($hdc)
  $b.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $b.Dispose()
  Write-Output ("CAPTURE: " + $ok + " " + $w + "x" + $h + " -> " + $path)
}

if ($NoLaunch) { Capture $shot; Write-Output "SHOT: $shot"; exit 0 }

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $Exe
$psi.UseShellExecute = $false
$psi.WorkingDirectory = Split-Path $Exe
if ($DataRoot) { $psi.EnvironmentVariables["BENTAGO_DATA_ROOT"] = $DataRoot }
if ($DotnetRoot -and (Test-Path $DotnetRoot)) { $psi.EnvironmentVariables["DOTNET_ROOT"] = $DotnetRoot; $psi.EnvironmentVariables["DOTNET_ROOT_X64"] = $DotnetRoot }
$p = [System.Diagnostics.Process]::Start($psi)

$ready = $false
$deadline = (Get-Date).AddSeconds(30)
$handle = 0
while (-not $ready -and (Get-Date) -lt $deadline) {
  Start-Sleep -Milliseconds 250
  $p.Refresh()
  if ($p.HasExited) { Write-Output ("EXITED early code=" + $p.ExitCode); exit 2 }
  if ($p.MainWindowHandle -ne 0) { $ready = $true; $handle = $p.MainWindowHandle }
}
if (-not $ready) { Write-Output "NOT_READY: no main window within 30s"; $p.Kill(); exit 3 }
Write-Output ("READY handle=" + $handle + " since=" + ((Get-Date).ToString("o")))
Start-Sleep -Milliseconds 800
if ($WindowShot) { CaptureWindow $handle $shot } else { Capture $shot }
Write-Output ("SHOT: " + $shot)

if ($Width -gt 0 -and $Height -gt 0) {
  # resize via win32 not available in PS 5.1 without Add-Type; report as-is
  Write-Output ("NOTE: requested " + $Width + "x" + $Height + "; capture is the virtual screen. Use OS resolution setting before running for layout lanes.")
}
# Optional auto-login capture: type username/TAB/password/ENTER into the
# foreground window (seeded admin/admin), then capture the signed-in state.
if ($AutoLogin) {
  try {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class BgWin32 {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
"@
    [BgWin32]::ShowWindow($handle, 9) | Out-Null
    $fg = [BgWin32]::SetForegroundWindow($handle)
    Start-Sleep -Milliseconds 800
    if (-not $fg) { Write-Output "AUTOLOGIN: SetForegroundWindow failed" }
    $wshell = New-Object -ComObject WScript.Shell
    $wshell.SendKeys($AutoLogin)
    Start-Sleep -Milliseconds 2500
    $signedShot = Join-Path $OutDir ("bg-001-" + $Lane + "-signedin.png")
    Capture $signedShot
    $before = (Get-FileHash $shot).Hash.Substring(0, 12)
    $after = (Get-FileHash $signedShot).Hash.Substring(0, 12)
    Write-Output ("SHOT: " + $signedShot + " (differs-from-login: " + ($before -ne $after) + ")")
  } catch {
    Write-Output ("AUTOLOGIN: " + $_.Exception.Message)
  }
}
# Deterministic finish: force-kill the app (SQLite tolerates it; the PNG is the
# evidence, and lane 10 proves recovery). CloseMainWindow is unreliable with
# the boot sequence, so a forced stop is the only deterministic close.
$p.Kill() | Out-Null
Start-Sleep -Milliseconds 500
Write-Output "CLOSED (killed)"
exit 0
