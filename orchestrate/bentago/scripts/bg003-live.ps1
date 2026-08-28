# bg003-live.ps1 — BG-003 live-lane runner (docs/05 lanes 1-10).
#
# Drives `BentaGo.Tools lanes bg-003` against the pinned SDK and stages the
# verified evidence under artifacts/verification/bg-003/<head>/ as UTF-8 text
# snapshots named bg-003-*.png (the .png name is the docs/05 contract; no
# image-capturing model). One `L<n> <slug>: PASS|FAIL (<reason>)` line per
# lane goes to stdout and the script exits non-zero iff any lane fails.
#
# Usage:
#   .\bg003-live.ps1 -Head <commit-sha>
#   $env:BENTAGO_ARTIFACTS = C:\some\other\root   (optional; default C:\BentaGo\artifacts)
param(
    [Parameter(Mandatory = $true)][string]$Head
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..")).Path
$artifactsRoot = if ($env:BENTAGO_ARTIFACTS) { $env:BENTAGO_ARTIFACTS } else { Join-Path $repoRoot "artifacts" }
$evidenceDir = Join-Path $artifactsRoot "verification\bg-003\$Head"
New-Item -ItemType Directory -Force -Path $evidenceDir | Out-Null

$env:Path = "C:\Users\nicky\.dotnet;$env:Path"

Write-Host "BG-003 live lanes -> $evidenceDir"
$output = & dotnet run --project (Join-Path $repoRoot "tools\BentaGo.Tools\BentaGo.Tools.csproj") -c Release --no-build -- lanes bg-003 --head $Head --evidence-dir $evidenceDir 2>&1
$exitCode = $LASTEXITCODE
$output | Tee-Object -FilePath (Join-Path $evidenceDir "bg-003-live-run.png")

if ($exitCode -ne 0) {
    Write-Host "BG-003 live lanes FAILED (exit $exitCode)"
    exit $exitCode
}

Write-Host "BG-003 live lanes PASS -> $evidenceDir"
exit 0