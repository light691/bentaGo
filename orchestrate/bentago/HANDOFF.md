# BentaGo Program Handoff

**For:** the next session taking over this program. Start with `/poteto-mode`, read this file in full, then act.
**Author session:** the coordinator that ran BG-001 through BG-008 and took the store-POS pivot decision.
**Handoff date (session time):** 2026-08-30.

## 1. The goal (recreate it in the new session)

Same-session goal tools do not carry over. In the new session call `create_goal` with this objective:

> Pivot BentaGo from the peddling program to a standalone store POS (no trips/peddlers/loads; one till; day lifecycle; receiving; customer credit/utang; corrections; reports; design overhaul). Sequence: BG-P0 rewrite docs/01+docs/05 to the store-POS domain; BG-P1 domain pivot (delete trip/peddler/load, shift day lifecycle, schema reset); BG-P2 receiving; BG-P3 customer credit; BG-P4 tills (identity laid in, single-till MVP); BG-P5 design overhaul + first-run wizard + store profile; BG-009 packaging/recover/pilot. Verify every PR with unit, live, perf evidence. UI generation uses deepseek-v4-flash-vision-exp (glm-5.3-flash only when warranted). The program is done when the store-POS MVP passes the pilot and every earlier PR is merged.

## 2. The operator's locked decisions (do not re-litigate)

- BentaGo is **no longer a peddling system**. Delete trips, peddlers, loads, route selling, per-peddler locations, per-trip reconciliation, stock-load CSV exchange. These must not survive as live concepts in docs or code.
- **One store, one till** for the MVP. Multi-till is future only; at most one line of "multi-till ready later" in specs.
- Accounting unit = **the day (shift)**: open register in the morning, sell all day, close the drawer at night. Day close: expected cash = cash sales - cash refunds - approved cash expenses; compare to counted cash; label BALANCED/SHORTAGE/OVERAGE; optional physical count per product with variance and notes; close-statement print; reopen with approval (reason + approver; prior close remains).
- Stock enters via **purchases/receiving** (supplier, lines, unit cost, date, reference; per-line movement; draft vs submitted). One store warehouse. StockOnHand, low-stock, adjustment with reason.
- **Customer credit (utang)** stays: named customers, balance, pay-down, ledger; excluded from expected cash.
- Corrections: VOID / RETURN (references original receipt, never more than sold) / DAMAGE-LOSS / ADJUSTMENT, reason + approver.
- Catalog import stays (header-by-name, preview, row errors, immutable log). Exports: transaction detail, reconciliation, inventory, receivables CSV (RFC-4180, invariant, Asia/Manila).
- **Offline-first is the core promise**: local SQLite, no cloud, no network dependency, ESC/POS 80mm 42-column receipts (receipt = source of truth; header from store profile: name/address/contact/TIN), USB scanner, cash drawer kick.
- Roles: Cashier / Supervisor / Admin. Audit sensitive actions (actor, terminal, reason).
- Store profile + **first-run wizard** must be in the MVP.
- **Design overhaul is in scope** (BG-P5): shell + Register-first screen set, tokens, 1366x768 + 1920x1080, derived from the operator's prototype. Screen inventory: Register (hero), Day close (second), Corrections, Transactions, Stock, Receiving, Customers, Reports, Admin, Login/role picker, First-run wizard, Help overlay.
- Model routing: deepseek-v4-flash-vision-exp for everything by default, including UI generation (operator approved this explicitly). glm-5.3-flash only when a UI task warrants it. Verification always deepseek.

## 3. Program state (verified)

- Repo `light691/bentaGo`. `origin/main` head: **`1852df1`** (code) with ledger/perf rows pushed on top at **`d1a2441`**. Root checkout `C:\BentaGo` sits on `verify-bentago-skill` (old tree, tops out around BG-003).
- **BG-001..BG-008 merged**, all slices: PRs #18..#28. Full suite **457/457** at 1852df1, 0 warnings, format clean. Live lanes BG-006/BG-007/BG-008 all pass at their merged heads (L1-L10 for BG-008). Perf bg-007 PASS; bg-008 probe recorded at `orchestrate/bentago/perf/bg-008/perf.tsv`.
- **Open BG-008 items** (surface to operator, do not silently close): close-calc P95 1073ms vs rule <=1000ms (borderline FAIL; median 872ms, fold-bound); review video `bg-008-reconciliation.mp4` not recorded (no exe drive available in-session); 10 review PNGs exist under `artifacts/review/bg-008-*.png`.
- **BG-009 not started** (packaging/recover/pilot). It moves to the end of the new sequence.
- **BG-P0 was launched and cancelled before any write.** Branch `bgp0` exists at `d1a2441` with no commits; worktree `C:\BentaGo\.wt\bgp0` is clean. Safe to delete or reuse.

## 3b. State correction (2026-08-30, docs alignment pass)

Facts verified in this session. Where they contradict sections above, THIS section wins.

- **The `C:\BentaGo` root checkout and every worktree under it are GONE.** This machine has ONE clone: `D:\bentago`, on `main`, in sync with `origin/main` at `de1fb38`. There are no worktrees. Section 7's one-writer rule now reads: `D:\bentago` is the only checkout; create worktrees under `D:\bentago\.wt\<name>` from `origin/main` for code units, or work on short-lived branches here.
- **The BG-P1a checkpoint `b310de3` is LOST.** It was committed on branch `bgp1` inside the deleted worktree and was never pushed: `bgp1` exists neither locally nor on origin, and `b310de3` is not an object in this clone. BG-P1 restarts from the Core stage per `docs/05` unit BG-P1. The architect's delete/retarget inventory survives in section 8 below, and `docs/05` carries the unit scope.
- **The .NET SDK pinned by `global.json` (10.0.400) is NOT installed on this machine** (`dotnet` is not on PATH). Install it before resuming any build gate.
- **Engine:** `subagent` / `subagent_fork` verified working this session (a readonly docs-exploration delegate returned a full report). Delegation routes are now PINNED: profile patch `~/.dsh/profiles/web/cordis.patch.yml` fixes `agentOptions` (provider `opencode-go`, model `glm-5.3-flash`) for `subagent`, `subagent_fork`, and workflow workers, and the provider profile sets `reasoning: high` as the default effort for requests that name none. Every delegated child runs glm-5.3-flash high unless a request overrides explicitly.
- **Docs alignment pass (this session):** README, docs/00-04, the ADRs, `units.tsv`, `status.md`, `gates.md`, and `frontier.json` were corrected to the store-POS program. Historical records (`queue/` briefs, `perf/` logs, `drain/`, `decisions.tsv`, superseded ADR rows) were intentionally left as history. The pivot now has its own decision record: `docs/decisions/0002-store-pos-pivot.md`.

## 4. Next action: BG-P0 (docs rewrite, docs only)

Working branch `bgp0` from `origin/main`, worktree `C:\BentaGo\.wt\bgp0`. Docs-only PR, no code/tests/orchestrate edits.

1. Rewrite `docs/01-product-requirements.md` as the store-POS PRD per section 2: product definition, users/roles, operating model (day lifecycle, receiving, utang, corrections), reconciliation model (formula, variance labels, no silent corrections), functional scope (session, catalog, receiving, sales incl. split payment + change, corrections, day close and reports, import/export, store profile + first-run wizard, devices), business rules, MVP inclusion, hardware profile, non-goals (no multi-till sync in MVP, no cloud, no loyalty, no online payments). Reference mode: dry, complete, sure. Explicit rules: integer centavos, Asia/Manila, offline-first.
2. Rewrite `docs/05-pstack-program-execution-plan.md` as the new execution plan for BG-P0..BG-P5, BG-009 with per-slice scope, unit gates, live-lane gates (name lanes concretely: day-close balanced/short/over, receiving draft->submit, utang pay-down, print after day close, 42-col fit, CSV correctness, first-run wizard, restart persistence), perf rules, evidence conventions, verification discipline (build/test/format gates, env-gated lanes, evidence dirs `artifacts/verification/bg-pN/<BENTAGO_HEAD>/`, review PNGs, operator review gate for interaction-changing PRs), one-writer and branch-from-origin/main rules, standing delegation note.
3. One-liner cross-reference in docs/05 that docs/02-04 stale references to trips/peddlers are superseded (updated in BG-P1/P5).
4. Gates: `git diff --stat` shows docs only; zero occurrences of peddl/peddler/trip-as-live-concept in the two files. Commit: `BG-P0: rewrite PRD + program plan to the store-POS domain (one till, day lifecycle, receiving, utang, design overhaul)`.
5. Then push branch, open PR to main, squash-merge per standing delegation, add ledger row led by the pattern below.

## 5. The design prompt v2 (operator asked for it; deliverable that survives)

The operator will provide the design prototype. The prompt they were given (regenerate exactly this for the design work):

```
DESIGN TASK: Full design system + screen set for BentaGo, a standalone,
offline-first Windows POS for a single Philippine store.

PRODUCT CONTEXT
- One Windows machine at the store counter. 1366x768 min, 1920x1080 target.
- One till, one cash drawer, one operator at a time.
- USB barcode scanner + 80mm thermal printer (ESC/POS, 42 columns).
  Keyboard-first, every action tappable/clickable.
- Fully offline. SQLite on disk. The register opens in the morning, sells
  all day, closes the drawer at night. Day close compares expected cash
  to counted drawer: BALANCED / SHORTAGE / OVERAGE.
- Money is integer pesos/centavos; totals large, right-aligned, no
  overflow. Products: sardines, instant coffee, charcoal, soap.
  Payments: Cash, GCash, Maya, Bank Transfer, Credit/Utang, Split.
- Users: Cashier, Supervisor, Admin. Real approvals with reason:
  voids, damage, adjustments, post-close corrections.
- Speed beats beauty. Errors are plain language ("Printer needs
  attention"). Receipts fit 80mm/42 columns; receipt is source of truth.

SCRIPTS must design, in this order:
1. Register (sale/checkout) - the 90% screen: scan field, cart with line
   breakdowns, subtotal/total/change large, payment methods, checkout with
   print. States: normal, scanner-active, printer-down. Cart editing.
2. Day close / reconciliation - expected vs counted cash, optional
   count, variance banner, notes, reopen-with-approval, close-statement.
   States: balanced, shortage, overage.
3. Corrections - void/return/damage/adjustment w/ reason + approver.
4. Transactions - day/period list, totals, reprint, lookup by number/scan.
5. Stock - on-hand, low-stock, receiving history, adjustment.
6. Receiving (purchases) - supplier, lines with cost, date, reference.
   Draft vs submitted state.
7. Customers (utang) - list, balance, pay-down, ledger.
8. Reports - daily sales, payment breakdown, receivable, exceptions.
   Printable, not BI dashboards.
9. Admin - users/roles, catalog import (CSV preview + row errors), store
   profile, devices (printer test, scanner diagnostics, drawer test).
10. Login/role picker - username/password, role, day-open state.
11. First-run wizard - store profile, admin account, printer+scanner test.
12. Help overlay - F-key reference.

DESIGN SYSTEM: token set (color, type, 8px grid XS/S/M/L, radii,
elevations, states: primary/danger/success/balanced/warning/shortage/
disabled/focused/error) as table + JSON/TS. Two layout levels. Shell
chrome: store name, date, till identity, offline indicator, printer
status, day-open/closed. True 80mm/42-col receipt mockups (sale, close,
return/reprint). Large targets, high contrast, no decorative gradients.

DELIVERABLES: three distinct directions (A. dense dark register console
for a veteran cashier; B. clean light kiosk for touch-first; C. paper/ink
neutral for budget store), one hero screen (register) each at 1366x768,
one-paragraph rationale each, recommend one. Then full screen set 2x PNG
both sizes, token table + JSON, receipt mockups, one-page state matrix.
No web-dashboard look, no e-commerce mockup, no "sync later" language.
```

## 6. Engine facts (verified 2026-08-30, after engine fix)

- `subagent` and `subagent_fork` **now work** (engine fixed; PONG/FORK-PONG probes returned). Earlier in the program they returned nothing because the default child-model route was dead; that limitation no longer applies. Use plain subagents freely.
- The `workflow` tool's `agent()` also works and accepts explicit `provider` / `model` (`opencode-go-2`, `deepseek-v4-flash-vision-exp`), which is still the way to force a specific model per worker. Verification always deepseek.
- Fan-out pattern: one workflow run per slice (or plain subagent calls in parallel with run_in_background), phase() per parallel slice, Promise.all for parallel slices, return a plain JSON object.
- Old idle stored subagents from prior phases exist in the authoring session's tree; they are `[ready]` (idle storage, inert, only wake on send_message) and do not transfer to a new session. A new session starts with zero children.

## 7. Standing rules (locked, verified)

- **ONE-WRITER**: root checkout `C:\BentaGo` on `verify-bentago-skill` holds only operator/Codex and orchestrate edits. NEVER write code there. All code in worktrees `C:\BentaGo\.wt\<name>` created with `git -C C:\BentaGo fetch origin main -q; git -C C:\BentaGo worktree add C:\BentaGo\.wt\<name> -b <branch> origin/main`.
- Worktrees stack up; prune merged ones with `git worktree remove`.
- Gates per PR: `dotnet build -c Release` 0 errors 0 warnings; full `dotnet test -c Release` green; `dotnet format --verify-no-changes` 0 diagnostics; usings alphabetical per group; zero-literal XAML rule (no raw Thickness/Margin numerics; `StaticResource Thickness.XS/S/M/L` or `Thickness.RowGap/ColumnGap`); NO raw `Foreground=`/`Background=`/`FontSize=` literals in new markup.
- Commands must use the pinned SDK: `$env:Path = "C:\Users\nicky\.dotnet;$env:Path"`.
- `gh` = `C:\Program Files\GitHub CLI\gh.exe`; repo `light691/bentaGo`; OAuth app lacks `workflow` scope; no auto-merge; required check = `build`.
- Live lanes: env `BENTAGO_LIVE_LANES=1`, `BENTAGO_HEAD=<sha>`; evidence verdicts/screenshots from lane evidence classes (`Bg007LaneEvidence`, `Bg008LaneEvidence`); artifacts under `artifacts/verification/bg-pN/<head>/` and `artifacts/review/`.
- Never mid-flight correct a worker (respawn instead); builders verify by building only; content-exact transcription for imports; explicit-path commits; one writer per worktree.
- Money = integer PHP centavos; timezone Asia/Manila for date boundaries.
- Ledger `orchestrate/bentago/ledger.tsv` rows double as the program verdict log; push via the `.wt\ledger` worktree commit to `HEAD:main`. Perf tsvs go in `orchestrate/bentago/perf/<unit>/perf.tsv`.
- Standing delegation from operator (2026-08-30): squash-merge units at merge-ready; stop at crucial gates (palette/brand/scope changes, real-device passes, review-gate clicks, anything irreversible). "Merge and continue, don't wait."

## 8. Codebase facts for BG-P1 (verified)

- `ObservableObjectFactory.cs:54` registers `IAppDataSource, ScenarioDataSource` (in-memory BG-002 scenario) as the ONLY screen-data provider. The comment says "BG-005: swap this line only". A real **SqliteAppDataSource** was NEVER built: PeddlerHome and Stock read `AppSnapshot` from the scenario while transactional services write SQLite. This is the central standalone gap and the first thing BG-P5/BG-P1 must close.
- `MainViewModel.cs:54` hardcodes `SimulatedLabel = "Simulated data - BG-002 scenario"`; also `MainWindow.xaml:149` and `HelpOverlayView.xaml:71`. All must die in the pivot.
- Movement identity is a single global `sequence: long` (`src/BentaGo.Core/Domain/Ledger/Moves.cs`); audit already records `Terminal: Environment.MachineName` (`UseCaseServices.cs`). Multi-till (future) needs per-till sequence/GUID; the decision was laid out in the pivot plan section 2.
- Files for BG-P1 to delete or convert (inspect before deleting): `TripLifecycle.cs`, `TripAudit.cs` (TRIP.* events), `LoadCommand`, `CloseBuilder`/`ReopenBuilder`/`CountBuilder`/`CancelBuilder` trip forms, `DailyClose`/`PhysicalCount`/`CountLine` trip-scoped forms, `LedgerState.TripCash`/`Breakdown` per-location, `ITripReadService`, `TwoPeddlersScenario.cs`, `TripLifecycleScenario.cs`, `Bg008ReconciliationLanes` two-peddler lane, peddler home/trips screens, tests family for trip lifecycle. Keep: ledger fold math, receipts/printing pipeline, corrections, catalog import, permissions/audit, back/restore, reconciliation math at day granularity.
- Current design reference files (superseded by operator's new prototype): `designs/bentago-wpf-mvp-screen-set.png`, `designs/bentago-mvp-screen-set.png`.

## 9. Resume point

**SUPERSEDED 2026-08-30 by section 3b (docs alignment pass).** The block below claimed BG-P1 was in flow on branch `bgp1` with checkpoint `b310de3`. That branch was never pushed and the worktree that held it is gone; the checkpoint is unrecoverable. Section 3b and this section's current text are the authority.

**Current state (2026-08-30, after docs alignment):**
- main = `de1fb38`, in sync with origin. BG-P0 merged (#29, `7a98e1b`). BG-P2 brief staged (`orchestrate/bentago/inbox/BG-P2-receiving.md`); it launches only after BG-P1 merges.
- **BG-P1 restarts from the Core stage** (P1a): day-lifecycle Core, movement kinds (`GoodsReceived`, `FundOut`), schema reset toward ONE baseline migration, then P1b application layering, P1d real `SqliteAppDataSource` swap at `ObservableObjectFactory.cs:54` + kill `SimulatedLabel`, P1e tools/tests/day lanes. Scope and gates: `docs/05` unit BG-P1. Deletion/retarget inventory: section 8. Application-layer brief: `orchestrate/bentago/inbox/BG-P1b-application.md` (recreated this session from section 9's notes; the original brief file was lost with the old checkout).
- **Before any build:** install the .NET SDK pinned in `global.json` (10.0.400); `dotnet` is not on PATH on this machine.
- Open items surfaced, not closed: BG-008 close-calc P95 1073ms vs rule <=1000ms (optimize fold or accept, documented); `bg-008-reconciliation.mp4` never recorded; real-device pass and client real CSV exports remain release gates (`gates.md`).

**Earlier resume text (sections 1-8 above for locked decisions and standing rules; environment paths there predate this machine).**
