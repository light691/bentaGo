# verify-bentago doctor. Read-only instance health check.
# Exit code 0 means the instance is worth driving. Warnings do not fail.
param(
  [string]$Dotnet = "C:\Users\nicky\.dotnet\dotnet.exe",
  [string]$Exe = "C:\BentaGo\src\BentaGo.App\bin\Release\net10.0-windows\BentaGo.App.exe"
)
$fail = $false

if (-not (Test-Path $Dotnet)) {
  Write-Output ("DOCTOR FAIL: SDK at " + $Dotnet + " not found; the repo pins 10.0.400 and PATH dotnet is 8.0.419")
  $fail = $true
} else {
  $v = & $Dotnet --version
  if ($v -ne "10.0.400") {
    Write-Output ("DOCTOR FAIL: SDK " + $v + "; global.json pins 10.0.400")
    $fail = $true
  } else {
    Write-Output "DOCTOR OK: dotnet 10.0.400"
  }
}

if (-not (Test-Path $Exe)) {
  Write-Output "DOCTOR FAIL: exe missing; run `& $Dotnet build -c Release C:\BentaGo\BentaGo.sln` first"
  $fail = $true
} else {
  Write-Output ("DOCTOR OK: exe " + $Exe)
}

$running = @(Get-Process BentaGo.App -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
  Write-Output ("DOCTOR WARN: BentaGo.App running (pid " + $running[0].Id + "); a previous drive may not have cleaned up. Kill that pid, not by name.")
} else {
  Write-Output "DOCTOR OK: no stray BentaGo.App process"
}

$realDb = "C:\ProgramData\BentaGo\data\bentago.db"
if (Test-Path $realDb) {
  Write-Output ("DOCTOR WARN: real instance data exists at " + $realDb + "; drives must set BENTAGO_DATA_ROOT and never touch this")
} else {
  Write-Output "DOCTOR OK: real data root untouched"
}

if ($fail) { exit 1 }
Write-Output "DOCTOR RESULT: clean"
exit 0
