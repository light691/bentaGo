# Verdict recipe: BG-001 (merge-ready)

Run at the exact head SHA after bg-001b lands. Lanes:

1. **Gates lane (coordinator-inline).** Check every box in docs/05 BG-001 Files/Build/You see: solution and six projects exist; global.json pins 10.0.400 (dotnet --version == 10.0.400 in repo root); data-path/logging/config/DI/migration/seed/backup/restore exist; 1366x768 WPF shell; no domain entities beyond AppSettingKeyValue; no cloud/API/third-party UI; no secrets/DBs in git.
2. **Unit lane (coordinator-inline).** $env:Path prepend; dotnet format --verify-no-changes; dotnet build -c Release; dotnet test -c Release --filter Foundation; dotnet run --project tools/BentaGo.Tools -c Release -- foundation verify. Receipt block verbatim into ledger.
3. **Live lanes (coordinator harness).** Headless lanes 5-9 via foundation subcommands (backup, restore, corrupt-refuse, double-migrate). UI lanes 1-4, 10 via orchestrate/bentago/scripts/bg001-live.ps1 at 1366x768 and 1920x1080. Lane 8 (read-only data dir) via a copied test root with ACL removed. Screenshots to artifacts/verification/bg-001/<sha>/lane-<n>/. Review-gate copies: artifacts/review/bg-001-shell.png, bg-001-shell-1080.png. Video lane: blocked (no recorder on host), document adaptation with screenshot sequence in the ledger row.
4. **Perf lane (coordinator probe).** orchestrate/bentago/scripts/bg001-perf.ps1 -Runs 10 with warm-up. Median <= 5s, no run > 8s. Also perf cold start; log all runs to artifacts/verification/bg-001/<sha>/perf.tsv. Median becomes the program startup baseline.
5. **Audit lane (subagent, distrusts the PR body).** One read-only reviewer agent, brief: review `git diff main...bg-001 --stat` + full diff; check for: invented business rules, forbidden fields touched, secrets/db files, domain entities beyond the placeholder, unsafe backup/restore (active DB touched on corrupt input), WPF code-behind business logic, unreported deviations. Report PASS/ISSUES with evidence lines. Different model from the builder if the platform allows.

Verdict rule: clean only when lanes 1, 2, 5 PASS and live/perf lanes are either PASS or explicitly blocked-with-documented-deviation (no Computer Use, no recorder, no hardware). On ISSUES: send findings back to the builder owner; new head SHA resets verification.

Ledger row shape: unit, pr(branch), head, verdict, verifier, commands, screenshots, metric, baseline, verdict text.
