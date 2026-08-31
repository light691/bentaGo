# PAUSED 2026-08-31 — resume instructions

Operator ordered a stop. Resume = start a session, read this file + HANDOFF.md (sections 3b, 9) + preferences.md, then `create_goal` with HANDOFF section 1's objective and continue the queue.

## State at pause

- **BG-P1 MERGED** as PR #31, merge commit `19423ba` on origin/main. Full suite 429/429, build 0/0, format clean, trip vocabulary grep 0. Coordinator harness VERDICT PASS at defb947, independently rerun.
- **P1e IN FLIGHT, uncommitted salvage in worktree `D:\bentago\.wt\bgp1e` (branch bgp1e at 19c6e54):**
  - `tests/BentaGo.Tests/AppShell/LiveLanes/BgP1DayLanes.cs` — five day lanes written.
  - `BgP1LaneEvidence.cs`, `BgP1ReviewPack.cs` — evidence + screenshot classes.
  - `tests/BentaGo.Tests/Domain/BgP1PerfProbe.cs` — DONE: fold 100k P95 63ms vs <=1000ms rule, PASS, recorded in `orchestrate/bentago/perf/bg-p1/perf.tsv` (retires the old BG-008 close-calc 1073ms worry).
  - `artifacts/verification/bg-p1/` — lane evidence JSON+PNG for at least day-close-balanced/short/over, day-reopen (25 files in artifacts/).
  - NOT done: lane runs verified end-to-end, review-pack screenshots (artifacts/review/bg-p1-*.png), commit, push, PR.
- Two P1e workers stopped silently mid-unit (no closing reports). Root cause unknown; suspect the live-lane runs (BENTAGO_LIVE_LANES spawns real WPF windows) hang or exceed the timebox. On resume: prefer running the lanes directly in-context (coordinator) or with a longer timebox, and commit the salvage FIRST so nothing is lost again.

## Resume steps

1. `create_goal` per HANDOFF section 1 (append: design HELD until prototype lands; coordinator auto-merges per preferences.md order 13).
2. In `D:\bentago\.wt\bgp1e`: build, fix the new lane files if needed, run the five lanes (`BENTAGO_LIVE_LANES=1`, `BENTAGO_HEAD=<head sha>`), capture review PNGs to artifacts/review/bg-p1-*.png, commit with explicit paths, push origin bgp1e, PR, coordinator squash-merges (standing order 13).
3. Ledger row for P1e, then launch BG-P2 from `orchestrate/bentago/inbox/BG-P2-receiving.md` (brief is corrected and ready).
4. Queue after BG-P2: BG-P3 (`inbox/BG-P3-utang.md`), BG-P4 (till identity — note the review's cut candidate: folding till_id into the schema was NOT taken; P4 stands per docs/05), BG-P5 (HELD on operator prototype), BG-009.

## Gates at pause

- Design prototype: HELD by operator (in progress).
- Merge delegation: active — coordinator auto-merges merge-ready PRs.
- Nothing else awaiting the operator.
