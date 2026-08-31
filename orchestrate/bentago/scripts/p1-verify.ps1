# BG-P stage verify harness. One command in, one verdict out.
# Usage: powershell -File orchestrate\bentago\scripts\p1-verify.ps1 [-Worktree D:\bentago\.wt\bgp1] [-CoreOnly]
# Runs: build (Release) -> format check -> grep gates -> tests (Core bucket only, or full).
# Emits PASS/FAIL per line and a final verdict. Exit 0 = all gates green.

param(
    [string]$Worktree = "D:\bentago\.wt\bgp1",
    [switch]$CoreOnly
)

$ErrorActionPreference = 'Continue'
$env:Path = "$env:USERPROFILE\.dotnet;$env:Path"
$fail = 0
function Verdict($name, $ok, $detail) {
    $tag = if ($ok) { 'PASS' } else { 'FAIL'; $script:fail++ }
    Write-Output ("{0} {1} {2}" -f $tag, $name, $detail)
}

Write-Output "== build =="
$build = & dotnet build "$Worktree\BentaGo.sln" -c Release --nologo 2>&1
$errs = ($build | Select-String 'error ').Count
$warns = ($build | Select-String 'warning ').Count
Verdict 'build' (($errs -eq 0) -and ($warns -eq 0)) "errors=$errs warnings=$warns"

Write-Output "== format =="
$fmt = & dotnet format "$Worktree\BentaGo.sln" --verify-no-changes 2>&1
Verdict 'format' ($LASTEXITCODE -eq 0) "exit=$LASTEXITCODE"

Write-Output "== grep gates (live concepts) =="
$g1 = & git -C $Worktree grep -inE "peddl|\btrips?\b" -- src/BentaGo.Core
Verdict 'grep-core-trip-peddl' ($g1.Count -eq 0) "hits=$($g1.Count)"
$g2 = & git -C $Worktree grep -inE "peddl" -- src tests tools
Verdict 'grep-solution-peddl' ($g2.Count -eq 0) "hits=$($g2.Count)"

Write-Output "== tests =="
$filter = if ($CoreOnly) { '--filter', 'FullyQualifiedName!~BentaGo.Tests.Application&FullyQualifiedName!~BentaGo.Tests.App' } else { @() }
$test = & dotnet test "$Worktree\BentaGo.sln" -c Release --nologo @filter 2>&1
$line = ($test | Select-String 'Passed!|Failed!').Line
Verdict 'tests' ($LASTEXITCODE -eq 0) "$line"

if ($fail -eq 0) { Write-Output 'VERDICT PASS' } else { Write-Output "VERDICT FAIL ($fail gates red)" }
exit ($fail -eq 0 ? 0 : 1)
