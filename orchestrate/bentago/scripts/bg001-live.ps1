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

if ($NoLaunch) { Capture $shot; Write-Output "SHOT: $shot"; exit 0 }

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $Exe
$psi.UseShellExecute = $false
$psi.WorkingDirectory = Split-Path $Exe
if ($DataRoot) { $psi.EnvironmentVariables["BENTAGO_DATA_ROOT"] = $DataRoot }
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
Capture $shot
Write-Output ("SHOT: " + $shot)

if ($Width -gt 0 -and $Height -gt 0) {
  # resize via win32 not available in PS 5.1 without Add-Type; report as-is
  Write-Output ("NOTE: requested " + $Width + "x" + $Height + "; capture is the virtual screen. Use OS resolution setting before running for layout lanes.")
}
# leave the window open briefly for the operator to see, then close
Start-Sleep -Seconds 2
$p.CloseMainWindow() | Out-Null
Start-Sleep -Seconds 2
if (-not $p.HasExited) { $p.Kill() | Out-Null }
Write-Output "CLOSED"
exit 0
