BG-P1e: Tools + Tests + day lanes (BG-P1 verify stage)
Status: queued (launch after P1d merges; last stage of BG-P1)
Author: coordinator, 2026-08-30

PREREQS
- P1a Core, P1b Application, P1c schema baseline, P1d real data source all
  merged. Read orchestrate/bentago/preferences.md, docs/05 BG-P1 verify box.

SCOPE
1. Tools: retarget BentaGo.Tools (inspect/seed utilities) to the day
   schema; the inspector reports the new tables and row counts.
2. Tests: the FULL suite is green for the first time since the pivot. Every
   trip-era test is deleted or retargeted to day semantics. Golden fixtures
   rebuilt deliberately (the reconciliation golden changes because ADR 0004
   added float + FundIn + FundOut to expected cash). No silent fixture
   edits: the PR description lists every fixture that changed and why.
3. Day lanes (env BENTAGO_LIVE_LANES=1, BENTAGO_HEAD), the plan's named
   three plus restart: day-close-balanced (sell, close, BALANCED),
   day-close-short (cash missing -> SHORTAGE + amount), day-close-over
   (extra cash -> OVERAGE), day-reopen (admin approval, prior close
   visible), day-restart-persistence (kill after sale, day survives).
   Evidence to artifacts/verification/bg-p1/<BENTAGO_HEAD>/, review PNGs
   to artifacts/review/.
4. Perf probe: 100,000-movement fold P95 within docs/05's BG-P1 budget,
   recorded to orchestrate/bentago/perf/bg-p1/perf.tsv. Note: the review
   recommended demoting fantasy-scale budgets from merge-blocking gates;
   the coordinator treats a miss as an issue to record, not an automatic
   block, pending the operator's call.

ACCEPTANCE
- Full `dotnet test -c Release` green; build 0/0; format clean.
- All five lanes PASS with PNG evidence at the merged head.
- `git grep -inE "peddl|\btrips?\b" -- src tests tools` = 0.

WORKFLOW
- Worktree D:\bentago\.wt\bgp1e, branch bgp1e from origin/main. Push when
  green; coordinator squash-merges (standing order 13).
COMMIT
BG-P1e: tests + day lanes green; suite fully retargeted to the store POS
