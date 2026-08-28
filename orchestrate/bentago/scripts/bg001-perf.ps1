# BG-001 cold-start perf probe (coordinator-owned verification tool)
# Usage: .\bg001-perf.ps1 -Exe <path> -DataRoot <tempdir> [-Runs 10]
# Output: TSV lines to stdout: run, elapsedMs, windowSeen
param(
  [Parameter(Mandatory = $true)][string]$Exe,
  [string]$DataRoot = "",
  [int]$Runs = 10
)
$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force -Path $DataRoot | Out-Null
function ProbeOnce([int]$n, [string]$label) {
  $psi = New-Object System.Diagnostics.ProcessStartInfo
  $psi.FileName = $Exe
  $psi.UseShellExecute = $false
  $psi.WorkingDirectory = Split-Path $Exe
  if ($DataRoot) { $psi.EnvironmentVariables["BENTAGO_DATA_ROOT"] = $DataRoot }
  $sw = [System.Diagnostics.Stopwatch]::StartNew()
  $p = [System.Diagnostics.Process]::Start($psi)
  $deadline = (Get-Date).AddSeconds(30)
  while (-not $p.HasExited -and (Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 200
    $p.Refresh()
    if ($p.MainWindowHandle -ne 0) { break }
  }
  $sw.Stop()
  $seen = ($p.MainWindowHandle -ne 0)
  "{0}`t{1}`t{2}" -f $label, $sw.ElapsedMilliseconds, $seen | Write-Output
  if (-not $p.HasExited) { $p.CloseMainWindow() | Out-Null; Start-Sleep -Milliseconds 500; if (-not $p.HasExited) { $p.Kill() | Out-Null } }
  Start-Sleep -Milliseconds 400
  return $sw.ElapsedMilliseconds
}
Write-Output "run`telapsedMs`twindowSeen"
ProbeOnce 0 "warmup" | Out-Null
for ($i = 1; $i -le $Runs; $i++) { ProbeOnce $i $i }
Write-Output "DONE"
