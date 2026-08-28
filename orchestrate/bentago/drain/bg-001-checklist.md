# Drain checklist for BG-001 (merge-ready handoff)

Run at the completion notice. Order matters.

## 1. Verify at the head SHA

```powershell
$env:Path = "C:\Users\nicky\.dotnet;$env:Path"
cd C:\BentaGo
git switch bg-001
$head = git rev-parse HEAD
dotnet format --verify-no-changes
dotnet build -c Release
dotnet test -c Release --filter Foundation
dotnet run --project tools/BentaGo.Tools -c Release -- foundation verify   # or built exe
```

Record: build result, test counts, format result, proof receipt contents, head SHA.

## 2. Ledger row

Append to orchestrate/bentago/ledger.tsv:

```text
BG-001	-	<head-sha>	unit-test-verified	coordinator+reviewer	<ISO date>
```

Verdict stays `unit-test-verified` until the live and perf lanes have evidence. Live lanes for BG-001 are headless-adapted (foundation command + BootstrapTests cover lanes 1, 2, 5, 6, 7, 9 per their assertions). Layout lanes 3, 4 and the recovery lane 10 need the app running; see the live-lane pack below. Perf lane: coordinator runs the cold-start probe after the app exists.

## 3. Pilot review (contract falsification)

- Brief template: did the worker produce the expected report shape? Were forbidden fields empty?
- Verify recipe: were the commands runnable and did the outputs match the plan's assertions?
- Unit size: was the timebox right? Was the scope single-writer clean?
- File boundary: no edits outside the owned set?
- Write the pilot review row into decisions.tsv and fix preferences.md or the brief template now if anything was off.

## 4. Merge-ready handoff

- Interaction PR: stop here. Package artifacts/review/bg-001-shell.png and bg-001-shell-1080.png and the video per the plan. Post the report to the operator.
- Rebase onto main after the verdict and prove the patch content did not change (git diff main...bg-001 summary before and after rebase, same tree hash).
- Operator squash-merges. On merge: rebuild from main, rerun smoke, update frontier.json, then dispatch BG-002 and BG-004.

## 5. Live-lane operator pack (bg-001)

Operator runs these on this machine (no Computer Use path exists):

| Lane | Action | Pass | Artifact |
|---|---|---|---|
| 1 | Start app with no data dir | Login shell shows Ready; database created | artifacts/review/bg-001-first-start.png |
| 2 | Start a second time | No duplicate admin or migration | bg-001-repeat-start.png |
| 3 | Resize to 1366x768 | No clipped navigation or status text | bg-001-1366-layout.png |
| 4 | Resize to 1920x1080 | Aligned and usable | bg-001-1080-layout.png |
| 5 | Create a backup | File and success receipt name same DB version | bg-001-backup-created.png |
| 6 | Restore it | Seeded admin survives; pre-restore DB preserved | bg-001-restore.png |
| 7 | Select corrupt backup | Restore refuses; active DB unchanged | bg-001-corrupt-backup.png |
| 8 | Make data folder read-only | Clear error; exit without partial files | bg-001-data-path-error.png |
| 9 | Run migration twice (tools) | Both runs converge to same schema | bg-001-repeat-migration.png |
| 10 | Close after unclean stop | Intact DB opens, or concrete recovery error | bg-001-recovery-start.png |

Perf: `artifacts/verification/bg-001/<head>/perf-cold-start.tsv` with ten runs; median under five seconds, no run over eight.
