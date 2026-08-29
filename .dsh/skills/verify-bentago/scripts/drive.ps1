# verify-bentago drive. Runs the program harness (bg001-live.ps1) against an
# isolated BENTAGO_DATA_ROOT and captures evidence + side effects.
param(
  [Parameter(Mandatory = $true)][string]$Lane,
  [string]$Exe = "C:\BentaGo\src\BentaGo.App\bin\Release\net10.0-windows\BentaGo.App.exe",
  [string]$Harness = "C:\BentaGo\orchestrate\bentago\scripts\bg001-live.ps1",
  [string]$Keystrokes = "admin{TAB}admin{ENTER}",
  [string]$DataRoot = "",
  [switch]$KeepRoot
)
$ErrorActionPreference = "Stop"

$runId = Get-Date -Format "yyyyMMdd-HHmmss"
$evid = Join-Path "C:\BentaGo\artifacts\verification\verify-bentago" $runId
New-Item -ItemType Directory -Force -Path $evid | Out-Null
$log = Join-Path $evid "run.log"
$removable = $false

if ($DataRoot -eq "") {
  $scratch = Join-Path $evid "scratch"
  $dataRoot = Join-Path $scratch "data-root"
  New-Item -ItemType Directory -Force -Path $dataRoot | Out-Null
  $removable = $true
} else {
  $dataRoot = $DataRoot
  New-Item -ItemType Directory -Force -Path $dataRoot | Out-Null
}

Write-Output ("RUN " + $runId + " lane=" + $Lane + " dataRoot=" + $dataRoot) | Tee-Object -FilePath $log
& $Harness -Exe $Exe -OutDir $evid -Lane $Lane -DataRoot $dataRoot -AutoLogin $Keystrokes -WindowShot *>&1 | Tee-Object -FilePath $log
$harnessExit = $LASTEXITCODE
Write-Output ("HARNESS_EXIT " + $harnessExit) | Tee-Object -FilePath $log

$db = Join-Path $dataRoot "data\bentago.db"
$logsDir = Join-Path $dataRoot "logs"
$dbExists = Test-Path $db
Write-Output ("DB_EXISTS " + $dbExists) | Tee-Object -FilePath $log
if ($dbExists) {
  Write-Output ("DB_BYTES " + (Get-Item $db).Length) | Tee-Object -FilePath $log
}
Write-Output ("LOGS_ENTRIES " + @(Get-ChildItem $logsDir -File -ErrorAction SilentlyContinue).Count) | Tee-Object -FilePath $log
Write-Output ("EVIDENCE " + $evid) | Tee-Object -FilePath $log

if ($removable -and -not $KeepRoot) {
  Remove-Item -Recurse -Force (Join-Path $evid "scratch") -ErrorAction SilentlyContinue
  Write-Output "CLEANUP: scratch data root removed" | Tee-Object -FilePath $log
}
exit $harnessExit
