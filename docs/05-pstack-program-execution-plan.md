# BentaGo program execution plan (store POS)

BentaGo is the main point of sale for one Philippine store. The program protects inventory and money with an append-only movement ledger, atomic business transactions, and auditable reversals. One store, one till, one warehouse. The day is the accounting unit.

Six units deliver the store POS in dependency order from BG-P0 through BG-P5, then BG-009 packages, recovers, and passes the pilot. The operator reviews every interaction change and merges each interaction-changing PR. A coordinator prepares merge-ready PRs that the operator does not need to review.

Note. docs/02, docs/03, and docs/04 still describe the former route-selling domain with trips. Those references are superseded. Update them in BG-P1 and BG-P5.

## How to read this

One box is one unit of work. Every box names the evidence that checks it. A nested box is a sub-step of the box above it. Check a box only when its evidence exists, such as a file, log line, screenshot, test run, or SHA. The body is a how-to. The appendix sections explain and record.

Tests alone are not sufficient verification. A unit is verified only when its unit, live, and perf boxes are all checked.

The program runs the installed `$poteto-mode` `playbooks/orchestrate.md` workflow. The coordinator prepares merge-ready PRs. The operator reviews and merges interaction-changing PRs.

## Standing rules

These rules apply to every slice. Each slice inherits them and adds only what is specific.

### Build, test, and format gates

For every PR before the push that faces origin:

- `dotnet build -c Release` must report zero errors and zero warnings.
- `dotnet test -c Release` must be fully green.
- `dotnet format --verify-no-changes` must report zero diagnostics.
- Using directives are alphabetical within each group.
- New XAML uses no raw `Thickness`, `Margin`, `Foreground`, `Background`, or `FontSize` literals. It uses `StaticResource Thickness.XS`, `Thickness.S`, `Thickness.M`, or `Thickness.L`, and `Thickness.RowGap` or `Thickness.ColumnGap`.

Pin the SDK before every command.

```powershell
$env:Path = "C:\Users\nicky\.dotnet;$env:Path"
```

### Live lanes

Live lanes are gated by the environment so a normal build never runs them.

```powershell
$env:BENTAGO_LIVE_LANES = "1"
$env:BENTAGO_HEAD = "<head-sha>"
```

A live lane runs in its own isolated environment at the PR head. WPF lanes use the installed Computer Use skill and drive the app with keyboard, scanner, printer, and dialog input. Data lanes use a fresh copied database and a dedicated output directory. A missing control capability blocks the lane. Pass a lane only on the side effects the lane names, not on a clean screenshot alone.

### Evidence conventions

- Unit evidence lands under `artifacts/verification/bg-pN/<BENTAGO_HEAD>/`.
- Operator review PNGs land in `artifacts/review/` as `bg-pN-<scenario>.png`.
- Review videos land in `artifacts/review/` as `bg-pN-<scope>.mp4`.
- Never commit a generated password, secret, customer data, or a raw database.

### Review gate

An interaction-changing PR stops at merge-ready for the operator. The operator reviews the screenshots and the video, then merges. A PR that changes no interaction is coordinator-squash-merged at merge-ready.

### One-writer and branch rules

- The root checkout `C:\BentaGo` holds only operator and Codex edits and orchestrate edits. Never write code there.
- All code lives in a worktree. Create it from origin/main.

```powershell
git -C C:\BentaGo fetch origin main -q
git -C C:\BentaGo worktree add C:\BentaGo\.wt\<name> -b <branch> origin/main
```

- One writer per worktree. Never rebase another writer's branch.
- Branches come from origin/main, never from the root checkout.
- One owner creates or changes EF migrations at a time. Keep migrations sequential.
- Open the PR ready, never draft, with `gh pr create --draft=false`. `gh` is at `C:\Program Files\GitHub CLI\gh.exe`. Repo `light691/bentaGo`. Required check is `build`.

### Standing delegation note

The operator squash-merges interaction-changing PRs after the review gate. The coordinator squash-merges units at merge-ready. Stop at a crucial gate: a palette or brand change, a scope change, a real-device pass, a review-gate click, or anything irreversible. Otherwise, merge and continue without waiting.

### Read at the start of each slice

Read `orchestrate/bentago/HANDOFF.md` in full, then the execution plan from origin, then the live goal. The handoff is the authoritative record of locked decisions and of the inventory of route-selling files to delete. Never treat the handoff as a substitute for the plan.

## Dependency graph

```text
BG-P0 -> BG-P1 -> BG-P2 -> BG-P3 -> BG-P4 -> BG-P5 -> BG-009
```

BG-P1 also resets the schema, so BG-P2, BG-P3, and BG-P4 build on the reset schema. BG-P4 depends on the schema reset from BG-P1 and on the ledger types from BG-P3. BG-009 depends on the completed store POS from BG-P5.

## Program checklist

### Arm the program

- [ ] Present this plan and its six units to the operator. Stop until the operator gives an explicit execution instruction.
- [ ] Confirm the operator's locked decisions in `orchestrate/bentago/HANDOFF.md` section 2. Do not re-litigate a locked decision.
- [ ] Install the .NET 10 SDK and the Windows desktop SDK. Add `global.json` and prove that `dotnet --version` resolves the pinned SDK.
- [ ] Re-read the installed `$poteto-mode`, `$architect`, `$swarm`, `$interrogate`, `$no-comments`, and `$show-me-your-work` skills before the first dispatch.
- [ ] Create the goal with the text in `orchestrate/bentago/HANDOFF.md` section 1.
- [ ] Run BG-P0 as the first unit. Do not fan out until its brief, verification recipe, and merge-ready handoff pass review.

### Spawn owners

- [ ] Spawn one owner per ready unit. Give each owner one worktree, one branch, one file boundary, and the brief from this plan and from `docs/04-agent-delegation-contracts.md`.
- [ ] Hold the review gate. BG-P1, BG-P2, BG-P3, BG-P4, BG-P5, and BG-009 change an interaction. Each waits for the operator's screenshot and video review before merge. BG-P0 changes no interaction and is coordinator-squash-merged.
- [ ] Keep one writer for `BentaGo.Core`, `BentaGo.Application`, EF migrations, shared WPF resources, and package version files.
- [ ] Give read-only exploration a shared checkout. Give every writer an isolated worktree.

### Boot recipe for a live lane

- [ ] Check out the exact head SHA in the lane worktree.
- [ ] Run `dotnet restore`, `dotnet build -c Release`, and the unit-specific setup command.
- [ ] Copy the lane seed database to `artifacts/runtime/bg-pN/lane-<n>/bentago.db`. Never share a writable database between lanes.
- [ ] Start BentaGo with the lane data directory. Wait for the ready status and confirm that the process owns the expected database.
- [ ] Save screenshots under `artifacts/verification/bg-pN/<BENTAGO_HEAD>/lane-<n>/` and return the paths with the lane report.

## BG-P0. Rewrite the product requirements and the program plan

**Depends on.** None. This is the current slice.

**Scope.** Rewrite `docs/01-product-requirements.md` as the store-POS PRD. Rewrite `docs/05-pstack-program-execution-plan.md` as this plan. Do not touch `docs/02`, `docs/03`, or `docs/04`. Add the one-line cross-reference in this plan for the superseded references in those three docs. Docs only. No source code, no tests, no orchestrate edit, no push, no PR.

**Files.**

- [ ] `docs/01-product-requirements.md`
- [ ] `docs/05-pstack-program-execution-plan.md`

**Build.**

- [ ] Write the PRD in reference mode: dry, complete, sure. Reference mode describes facts for lookup. It does not instruct or persuade.
- [ ] Keep the PRD grounded in the store-POS pivot: single store, single till, single warehouse, day lifecycle, receiving, utang, corrections, store profile, and first-run wizard.

**You see.**

- [ ] A reader confirms BentaGo is the store's main point of sale with a day lifecycle, not a route-selling system. No route-selling concept survives as a live idea.

**Verify, unit.** Not applicable. This is a docs-only unit. The gate is the content check below.

**Verify, gates.** Run from the unit root.

- [ ] `git diff --stat` shows only `docs/01-product-requirements.md` and `docs/05-pstack-program-execution-plan.md` changed.
- [ ] `Select-String -Path docs\01-product-requirements.md,docs\05-pstack-program-execution-plan.md -Pattern ("ped"+"dl")` returns zero matches.
- [ ] `Select-String -Path docs\01-product-requirements.md,docs\05-pstack-program-execution-plan.md -Pattern ("tri"+"p")` returns zero matches as a live concept. The single allowed match is the cross-reference line in this plan.
- [ ] `Select-String -Path docs\01-product-requirements.md,docs\05-pstack-program-execution-plan.md -Pattern ("lo"+"ad")` returns zero matches as a live concept. A reference to importing or reading data is allowed.

**Verify, live.** Not applicable. Docs only.

**Review gate.** None. BG-P0 changes no interaction.

**Merge.**

- [ ] Coordinator squash-merges BG-P0 at merge-ready.

## BG-P1. Domain pivot: day lifecycle, one store, real data source

**Depends on.** BG-P0.

**Scope.** Delete every route-selling concept: per-seller stock locations, outbound stock issuance to a seller, route-level close, and per-seller reconciliation. Introduce the store day lifecycle: day open, day close, reopen-with-approval. One store location only. Reset the schema and regenerate the EF migrations. Retarget the former route-selling lane families to day-close lanes. Replace the in-memory scenario data source with a real `SqliteAppDataSource` and stop the app from reading the scenario on the sales screens. Remove the simulated-data label and let every screen read SQLite. Preserve the ledger fold math, the receipt and printing pipeline, corrections, catalog import, permissions and audit, backup and restore, and reconciliation math at day granularity. The authoritative inventory of route-selling files to delete or convert is in `orchestrate/bentago/HANDOFF.md` section 8.

**Files.**

- [ ] `src/BentaGo.Core/Domain/`
- [ ] `src/BentaGo.Application/`
- `ObservableObjectFactory.cs` currently registers `IAppDataSource` and `ScenarioDataSource` at line 54 as the only screen-data providers. Register `SqliteAppDataSource` instead.
- `MainViewModel.cs` line 54 hardcodes the simulated-data label. `MainWindow.xaml` line 149 and `HelpOverlayView.xaml` line 71 carry the same label. Delete all three.
- `src/BentaGo.Core/Domain/Ledger/Moves.cs` holds the movement identity as one global `sequence: long`. Leave it for BG-P4 per that unit's scope.
- `src/BentaGo.App/`
- `tests/BentaGo.Tests/`

**Build.**

- [ ] Model the day lifecycle as explicit states: open, closed, and reopened. A closed day is locked. A reopen requires a reason and an approver and leaves the prior close visible.
- [ ] Remove per-seller stock locations. Every inventory movement belongs to the one store warehouse.
- [ ] Reset the schema and regenerate the EF migrations. Do not carry a route-selling table or column into the store schema.
- [ ] Wire the sales screens to `SqliteAppDataSource`. A screen no longer reads in-memory scenario data.
- [ ] Keep the ledger fold math and the reconciliation math at day granularity.

**You see.**

- [ ] The app opens, starts a day, sells, closes the day, and reads every value from SQLite. No route-selling screen or record remains.

**Verify, unit.** Tests alone are not sufficient verification. A unit is verified only when its unit, live, and perf boxes are all checked.

- [ ] Tests cover day open, day close math, the `BALANCED`, `SHORTAGE`, and `OVERAGE` labels, reopen-with-approval, one-store location, ledger fold after the schema reset, and the absence of a route-selling table. Run `dotnet test -c Release --filter Domain`.

**Verify, live.** Tests alone are not sufficient verification. A unit is verified only when its unit, live, and perf boxes are all checked. Lanes run with `BENTAGO_LIVE_LANES=1` and `BENTAGO_HEAD=<sha>`.

- [ ] Lane 1. Day open. Save `bg-p1-01-day-open.png`. Pass when opening a day sets an Asia/Manila date and the register shows an open day.
- [ ] Lane 2. Day close balanced. Save `bg-p1-02-balanced.png`. Pass when expected cash equals counted cash and the banner label is `BALANCED`.
- [ ] Lane 3. Day close shortage. Save `bg-p1-03-short.png`. Pass when the shortage amount, sign, and required note are correct.
- [ ] Lane 4. Day close overage. Save `bg-p1-04-over.png`. Pass when the overage amount, sign, and required note are correct.
- [ ] Lane 5. One store location. Save `bg-p1-05-one-store.png`. Pass when stock is one warehouse with no per-seller balance.
- [ ] Lane 6. No route-selling remains. Save `bg-p1-06-no-route.png`. Pass when the navigation carries no route-selling screen and no menu exposes the route concept.
- [ ] Lane 7. Real data source. Save `bg-p1-07-datasource.png`. Pass when the sales screens read SQLite and no simulated-data label appears.
- [ ] Lane 8. Restart persistence. Save `bg-p1-08-persist.png`. Pass when a committed sale and an open day survive a restart and a closed day stays closed.
- [ ] Lane 9. CSV export correctness. Save `bg-p1-09-exports.png`. Pass when the transaction detail, reconciliation, and inventory CSV exports are RFC-4180 and reconcile to the screen.
- [ ] Lane 10. Catalog import. Save `bg-p1-10-import.png`. Pass when a header-by-name preview, leading-zero barcodes, row errors, and the immutable import log still work after the schema reset.

**Verify, perf.** Tests alone are not sufficient verification. A unit is verified only when its unit, live, and perf boxes are all checked.

- [ ] Metric. Time to evaluate 100,000 inventory movements into balances.
- [ ] Probe. Run the deterministic balance rebuild five times after one warm-up.
- [ ] Baseline. Record the median on the pinned host after the schema reset.
- [ ] Rule. Fail when the median exceeds one second or allocations exceed the recorded budget by 10 percent.

**Review gate.** BG-P1 changes interaction. The operator reviews before merge.

- [ ] Copy the day-open, balanced, shortage, overage, and one-store screenshots into `artifacts/review/bg-p1-<scenario>.png`.
- [ ] Record a 30 to 60 second video of a day open, a sale, a day close, and a restart. Save it as `artifacts/review/bg-p1-daycycle.mp4`.
- [ ] Post the screenshots and the video for the operator. Stop at merge-ready and wait for the operator's click.

**Merge.**

- [ ] Record a clean domain, interaction, datasource, and schema verdict at the exact BG-P1 head SHA.
- [ ] Rebase onto current `main` after the verdict and prove that the patch content did not change.
- [ ] The operator squash-merges BG-P1 after the review gate passes.

## BG-P2. Receiving

**Depends on.** BG-P1.

**Scope.** Purchases and receiving. A receiving record has a supplier, line items, unit cost, date, reference, and notes. Draft, then submitted. Each line commits a movement and raises on-hand stock. A submitted receiving is locked. Reopening it requires authorization. Stock exists in the one store warehouse. On-hand stock and the low-stock flag read from the movements.

**Files.**

- [ ] `src/BentaGo.Core/Domain/`
- [ ] `src/BentaGo.Application/`
- [ ] `src/BentaGo.App/Features/`
- [ ] `tests/BentaGo.Tests/`

**Build.**

- [ ] Model the receiving state machine: draft and submitted. Only a submitted receiving changes stock.
- [ ] Record a per-line movement with unit cost in integer centavos.
- [ ] Validate a receiving before submit: at least one line, a unit cost per line, a non-blank supplier, a valid date, and a unique reference.
- [ ] Raise on-hand stock by each submitted line and update the low-stock flag.

**You see.**

- [ ] An operator creates a receiving draft, adds lines with unit cost, submits, and sees on-hand stock rise by each line.

**Verify, unit.** Tests alone are not sufficient verification. A unit is verified only when its unit, live, and perf boxes are all checked.

- [ ] Tests cover the draft and submitted transitions, per-line movement, unit cost money math, reference uniqueness, zero-line rejection, immutability after submit, and the low-stock flag. Run `dotnet test -c Release --filter Receiving`.

**Verify, live.** Tests alone are not sufficient verification. A unit is verified only when its unit, live, and perf boxes are all checked. Lanes run with `BENTAGO_LIVE_LANES=1` and `BENTAGO_HEAD=<sha>`.

- [ ] Lane 1. Receiving draft. Save `bg-p2-01-draft.png`. Pass when supplier, lines, unit costs, date, and reference are editable.
- [ ] Lane 2. Receiving submit. Save `bg-p2-02-submit.png`. Pass when each line commits a movement and on-hand rises by line quantity.
- [ ] Lane 3. Draft to submitted. Save `bg-p2-03-state.png`. Pass when a draft is editable and a submitted receiving is locked.
- [ ] Lane 4. Validation. Save `bg-p2-04-validate.png`. Pass when zero lines or a missing unit cost is rejected before commit.
- [ ] Lane 5. Low-stock flag. Save `bg-p2-05-low-stock.png`. Pass when a product below its threshold shows the low-stock flag.
- [ ] Lane 6. Restart persistence. Save `bg-p2-06-persist.png`. Pass when a submitted receiving survives a restart and on-hand stock matches the movements.
- [ ] Lane 7. One store warehouse. Save `bg-p2-07-warehouse.png`. Pass when on-hand stock reads from the one store location.

**Verify, perf.** Tests alone are not sufficient verification. A unit is verified only when its unit, live, and perf boxes are all checked.

- [ ] Metric. Time to commit a receiving with 1,000 lines.
- [ ] Probe. Interleave ten receiving commits against the BG-P1 head and the BG-P2 head on the pinned host.
- [ ] Baseline. Record the BG-P1 single-movement commit time first.
- [ ] Rule. Fail when the P95 receiving commit exceeds one second.

**Review gate.** BG-P2 adds a receiving screen and changes interaction. The operator reviews before merge.

- [ ] Copy the draft, submitted, validation, low-stock, and warehouse screenshots into `artifacts/review/bg-p2-<scenario>.png`.
- [ ] Record a 30 to 60 second video of a draft to a submit and a low-stock flag. Save it as `artifacts/review/bg-p2-receiving.mp4`.
- [ ] Post the screenshots and the video for the operator. Stop at merge-ready and wait for the operator's click.

**Merge.**

- [ ] Record a clean receiving, movement, validation, and interaction verdict at the exact BG-P2 head SHA.
- [ ] Rebase onto current `main` after the verdict and prove that the patch content did not change.
- [ ] The operator squash-merges BG-P2 after the review gate passes.

## BG-P3. Customer credit, or utang

**Depends on.** BG-P2.

**Scope.** Named customers, an outstanding balance, a pay-down, and a ledger of every credit and payment. A credit sale in the payment dialog creates a receivable and is excluded from expected cash. A pay-down reduces the balance and appends a ledger row. A receivable report shows the sum of open balances. A receivable CSV export matches the report.

**Files.**

- [ ] `src/BentaGo.Core/Domain/`
- [ ] `src/BentaGo.Application/`
- [ ] `src/BentaGo.App/Features/`
- [ ] `tests/BentaGo.Tests/`

**Build.**

- [ ] Model a customer and a ledger as types that reject illegal states. Balance money in integer centavos.
- [ ] Require a customer for a Credit and Utang sale. Create the customer on the spot or select an existing one.
- [ ] Exclude a credit sale from expected cash.
- [ ] Reject a pay-down that exceeds the open balance.
- [ ] Append one ledger row for every credit and every payment, in order.

**You see.**

- [ ] An operator sells on credit to a named customer, sees the balance rise, then pays it down and sees the ledger.

**Verify, unit.** Tests alone are not sufficient verification. A unit is verified only when its unit, live, and perf boxes are all checked.

- [ ] Tests cover customer creation, balance math in integer centavos, the pay-down limit that rejects a negative balance, ledger append order, exclusion from expected cash, the receivable report, and receivable CSV correctness. Run `dotnet test -c Release --filter "Credit|Receivable"`.

**Verify, live.** Tests alone are not sufficient verification. A unit is verified only when its unit, live, and perf boxes are all checked. Lanes run with `BENTAGO_LIVE_LANES=1` and `BENTAGO_HEAD=<sha>`.

- [ ] Lane 1. Credit sale. Save `bg-p3-01-credit-sale.png`. Pass when a customer is required, a receivable is created, and expected cash excludes the total.
- [ ] Lane 2. Utang pay-down. Save `bg-p3-02-paydown.png`. Pass when the balance falls by the exact amount and the ledger gains a payment row.
- [ ] Lane 3. Ledger. Save `bg-p3-03-ledger.png`. Pass when each credit and each pay-down appears in order.
- [ ] Lane 4. Over pay-down. Save `bg-p3-04-overpay.png`. Pass when paying more than the open balance is rejected.
- [ ] Lane 5. Receivable report. Save `bg-p3-05-report.png`. Pass when the report equals the sum of open balances.
- [ ] Lane 6. Receivable CSV correctness. Save `bg-p3-06-exports.png`. Pass when the receivable CSV is RFC-4180 and matches the report.
- [ ] Lane 7. Restart persistence. Save `bg-p3-07-persist.png`. Pass when customers, balances, and the ledger survive a restart.

**Verify, perf.** Tests alone are not sufficient verification. A unit is verified only when its unit, live, and perf boxes are all checked.

- [ ] Metric. Time to render the receivable report with 50,000 customers.
- [ ] Probe. Run the report ten times against the BG-P3 head with a warm-up excluded.
- [ ] Baseline. Record the BG-P1 ledger aggregation time first.
- [ ] Rule. Fail when the P95 report render exceeds one second.

**Review gate.** BG-P3 adds a customers screen and changes interaction. The operator reviews before merge.

- [ ] Copy the credit sale, pay-down, ledger, report, and export screenshots into `artifacts/review/bg-p3-<scenario>.png`.
- [ ] Record a 30 to 60 second video of a credit sale and a pay-down. Save it as `artifacts/review/bg-p3-utang.mp4`.
- [ ] Post the screenshots and the video for the operator. Stop at merge-ready and wait for the operator's click.

**Merge.**

- [ ] Record a clean credit, receivable, ledger, exclusion, and interaction verdict at the exact BG-P3 head SHA.
- [ ] Rebase onto current `main` after the verdict and prove that the patch content did not change.
- [ ] The operator squash-merges BG-P3 after the review gate passes.

## BG-P4. Till identity

**Depends on.** BG-P1 and BG-P3.

**Scope.** Give the till an identity. The movement identity is one global `sequence: long` in `src/BentaGo.Core/Domain/Ledger/Moves.cs`. Audit already records `Terminal` from `Environment.MachineName` in `UseCaseServices.cs`. Introduce a per-till sequence or a till GUID, add `till_id` to movements and audits, and register the till at first run. Single-till MVP only. At most one line states that multi-till is future.

**Files.**

- [ ] `src/BentaGo.Core/Domain/`
- [ ] `src/BentaGo.Infrastructure/`
- [ ] `src/BentaGo.App/`
- [ ] `tests/BentaGo.Tests/`

**Build.**

- [ ] Register the till at first run and persist its id.
- [ ] Add `till_id` to every movement and every audit row.
- [ ] Use a per-till sequence or a GUID for movement identity so a future multi-till can keep unique references.
- [ ] Keep the till id stable across restart, and across backup and restore.

**You see.**

- [ ] First run registers the till, and every movement and audit carries the till id.

**Verify, unit.** Tests alone are not sufficient verification. A unit is verified only when its unit, live, and perf boxes are all checked.

- [ ] Tests cover till registration at first run, `till_id` on movements and audits, per-till sequence or GUID uniqueness, and till id stability across restart and across backup and restore. Run `dotnet test -c Release --filter "Till"`.

**Verify, live.** Tests alone are not sufficient verification. A unit is verified only when its unit, live, and perf boxes are all checked. Lanes run with `BENTAGO_LIVE_LANES=1` and `BENTAGO_HEAD=<sha>`.

- [ ] Lane 1. Till registered. Save `bg-p4-01-register.png`. Pass when first run stores a till id and shows it.
- [ ] Lane 2. Till id on data. Save `bg-p4-02-till-id.png`. Pass when every movement and audit carries the till id.
- [ ] Lane 3. Till identity across restart. Save `bg-p4-03-persist.png`. Pass when the till id is unchanged after a restart.
- [ ] Lane 4. Till identity after restore. Save `bg-p4-04-restore.png`. Pass when the till id is unchanged after backup and restore.
- [ ] Lane 5. Single till. Save `bg-p4-05-single.png`. Pass when only one till is active and the UI states that multi-till is future.

**Verify, perf.** Tests alone are not sufficient verification. A unit is verified only when its unit, live, and perf boxes are all checked.

- [ ] Metric. Time to allocate a movement reference.
- [ ] Probe. Run 100,000 movement allocations after one warm-up.
- [ ] Baseline. Record the BG-P1 single global sequence time first.
- [ ] Rule. Fail when the P95 reference allocation exceeds 50 microseconds or total time exceeds the recorded budget by 10 percent.

**Review gate.** BG-P4 changes the first-run interaction and touches till identity. The operator reviews before merge.

- [ ] Copy the till-register, till-id, and single-till screenshots into `artifacts/review/bg-p4-<scenario>.png`.
- [ ] Record a 30 to 60 second video of first run registering the till and a restart. Save it as `artifacts/review/bg-p4-till.mp4`.
- [ ] Post the screenshots and the video for the operator. Stop at merge-ready and wait for the operator's click.

**Merge.**

- [ ] Record a clean till-identity, audit, sequence, and interaction verdict at the exact BG-P4 head SHA.
- [ ] Rebase onto current `main` after the verdict and prove that the patch content did not change.
- [ ] The operator squash-merges BG-P4 after the review gate passes.

## BG-P5. Design overhaul, first-run wizard, and store profile

**Depends on.** BG-P4.

**Scope.** Rebuild the shell and the screen set around the Register as the hero screen. Derive the design from the operator's prototype. Deliver a token set for color, type, an 8px grid, radii, elevation, and states. Two layout levels. Shell chrome: store name, date, till identity, offline indicator, printer status, and day-open or day-closed. True 80mm, 42-column receipt mockups for a sale, a close, a return, and a reprint.

Screen inventory: Register (hero), Day close (second), Corrections, Transactions, Stock, Receiving, Customers, Reports, Admin, Login and role picker, First-run wizard, and Help overlay. Build at 1366 by 768 and 1920 by 1080.

Model routing: UI generation uses glm-5.3-flash when a UI task warrants it, otherwise deepseek-v4-flash-vision-exp. Verification is always deepseek-v4-flash-vision-exp.

**Files.**

- [ ] `src/BentaGo.App/`
- [ ] `src/BentaGo.App/Resources/`
- [ ] `tests/BentaGo.Tests/App/`

**Build.**

- [ ] Implement the Register-first screen set with the token set from the operator prototype. Remove the two earlier design reference files that the pivot superseded.
- [ ] Build the Login and role picker, the First-run wizard, and the Help overlay.
- [ ] Wire the First-run wizard to the store profile: name, address, contact, and TIN, plus an admin account and a printer and scanner test.
- [ ] Make the register keyboard-first: a dedicated scan input, explicit focus restoration after each scan, and a large cart, subtotal, total, and change.
- [ ] Make every screen keyboard-first with visible focus and a large touch fallback.

**You see.**

- [ ] The Register-first screen set renders at both target resolutions, the First-run wizard completes, and the receipts fit 42 columns.

**Verify, unit.** Tests alone are not sufficient verification. A unit is verified only when its unit, live, and perf boxes are all checked.

- [ ] Tests cover navigation, command enablement, focus restoration, shortcut conflicts, validation state, dialog results, token values, and view-model disposal. Run `dotnet test -c Release --filter AppShell`.

**Verify, live.** Tests alone are not sufficient verification. A unit is verified only when its unit, live, and perf boxes are all checked. Lanes run with `BENTAGO_LIVE_LANES=1` and `BENTAGO_HEAD=<sha>`.

- [ ] Lane 1. Register hero at 1366 by 768. Save `bg-p5-01-register-1366.png`. Pass when the cart, totals, and actions are visible without clipping.
- [ ] Lane 2. Register hero at 1920 by 1080. Save `bg-p5-02-register-1080.png`. Pass when spacing stays balanced and controls do not stretch.
- [ ] Lane 3. Day close screen. Save `bg-p5-03-dayclose.png`. Pass when the variance banner and the close statement match the day lifecycle.
- [ ] Lane 4. Print after day close. Save `bg-p5-04-close-print.png`. Pass when the close statement prints after a day close and the drawer opens.
- [ ] Lane 5. 42-column receipt fit. Save `bg-p5-05-receipt.png`. Pass when the sale, close, return, and reprint receipts fit the 80mm, 42-column width and wrap correctly.
- [ ] Lane 6. First-run wizard. Save `bg-p5-06-first-run.png`. Pass when the wizard collects the store profile, an admin account, and a printer and scanner test, then opens the Register.
- [ ] Lane 7. No clipping at both sizes. Save `bg-p5-07-layout.png`. Pass when no critical control clips or overlaps at either resolution.
- [ ] Lane 8. Store profile receipt header. Save `bg-p5-08-profile.png`. Pass when the receipt header uses name, address, contact, and TIN.
- [ ] Lane 9. Help overlay. Save `bg-p5-09-help.png`. Pass when the F-key reference appears and each documented shortcut has no conflict.
- [ ] Lane 10. Restart persistence with a completed first run. Save `bg-p5-10-persist.png`. Pass when a restart after a completed first run shows the Register and the store profile.

**Verify, perf.** Tests alone are not sufficient verification. A unit is verified only when its unit, live, and perf boxes are all checked.

- [ ] Metric. P95 navigation time from command to rendered destination.
- [ ] Probe. Measure 100 navigation changes after startup at both target resolutions.
- [ ] Baseline. Record the BG-P1 day-open navigation time first.
- [ ] Rule. Fail when the P95 navigation exceeds 150 milliseconds or managed memory grows by more than 20 MB after 500 changes.

**Review gate.** BG-P5 is the interaction overhaul. The operator reviews before merge.

- [ ] Copy the Register, Day close, Corrections, Transactions, Stock, Receiving, Customers, Reports, Admin, Login, First-run, and Help screenshots into `artifacts/review/bg-p5-<screen>.png`.
- [ ] Record a 30 to 60 second keyboard-only video of a First-run wizard, a sale, and a day close. Save it as `artifacts/review/bg-p5-design.mp4`.
- [ ] Post the screenshots and the video for the operator. Stop at merge-ready and wait for the operator's click.

**Merge.**

- [ ] Record a clean design, accessibility, layout, token, and interaction verdict at the exact BG-P5 head SHA.
- [ ] Rebase onto current `main` after the verdict and prove that the patch content did not change.
- [ ] The operator squash-merges BG-P5 after the review gate passes.

## BG-009. Package, recover, and pass the pilot

**Depends on.** BG-P5.

**Scope.** Velopack packaging, a self-contained Windows x64 installer, an update channel, backup and restore, startup recovery, and a pilot on target hardware.

**Files.**

- [ ] Publish settings and Velopack configuration
- [ ] Installer assets and update tests
- [ ] Pilot script and operations guide
- [ ] Hardening fixtures for upgrade, power interruption, large data, locked files, corrupt files, and backup restore

**Build.**

- [ ] Package a Windows x64 installer. Keep writable data under ProgramData.
- [ ] Run the update bootstrap before the WPF startup. Preserve and verify data across an upgrade and an uninstall.
- [ ] Automate the pilot and collect a signed evidence bundle with a database hash, the exports, screenshots, logs, a backup, and a restore report.

**You see.**

- [ ] A clean target laptop installs, runs fully offline, completes the pilot, upgrades, restores from backup, and produces the same reports.

**Verify, unit.** Tests alone are not sufficient verification. A unit is verified only when its unit, live, and perf boxes are all checked.

- [ ] Release tests cover publish layout, data-path separation, update bootstrap, migration backup, support-bundle redaction, uninstall preservation, and pilot assertions. Run `dotnet test -c Release --filter "Release|Pilot"`.

**Verify, live.** Tests alone are not sufficient verification. A unit is verified only when its unit, live, and perf boxes are all checked. Lanes run with `BENTAGO_LIVE_LANES=1` and `BENTAGO_HEAD=<sha>` on clean or restored Windows environments.

- [ ] Lane 1. Clean install. Save `bg-009-01-install.png`. Pass when BentaGo starts without an SDK or an administrator session.
- [ ] Lane 2. First-run wizard on a clean install. Save `bg-009-02-first-run.png`. Pass when data, logs, backup, and settings use the approved ProgramData folders.
- [ ] Lane 3. Fully offline. Save `bg-009-03-offline.png`. Pass when login, sale, print, day close, report, and backup never wait for a network.
- [ ] Lane 4. Upgrade. Save `bg-009-04-upgrade.png`. Pass when data and settings survive and migration runs once.
- [ ] Lane 5. Upgrade recovery. Save `bg-009-05-recovery.png`. Pass when the prior or the new version starts with valid data.
- [ ] Lane 6. Restore. Save `bg-009-06-restore.png`. Pass when the same reports and ledger totals return.
- [ ] Lane 7. Locked files. Save `bg-009-07-locked.png`. Pass when messages name the blocked file and no partial output remains.
- [ ] Lane 8. Day-close pilot. Save `bg-009-08-pilot.png`. Pass when the pilot sells, closes balanced, and reconciles on target hardware.
- [ ] Lane 9. Support bundle. Save `bg-009-09-bundle.png`. Pass when logs and metadata exist and passwords, PINs, customer details, and raw database content do not.
- [ ] Lane 10. Uninstall and reinstall. Save `bg-009-10-reinstall.png`. Pass when the approved data-retention choice is honored and the restored app reconciles.

**Verify, perf.** Tests alone are not sufficient verification. A unit is verified only when its unit, live, and perf boxes are all checked.

- [ ] Metric. Cold start, checkout P95, day close time, installer time, and peak working set on target hardware.
- [ ] Probe. Run the published self-contained build three times before and after packaging against the pilot dataset.
- [ ] Baseline. Use the accepted BG-P1, BG-P2, BG-P3, and BG-P5 metrics at their merged SHAs.
- [ ] Rule. Fail when startup exceeds five seconds, checkout exceeds two seconds, any metric regresses by more than 10 percent, or peak working set exceeds the target-hardware budget.

**Review gate.** BG-009 is a real-device and pilot unit. The operator reviews before merge.

- [ ] Copy the install, offline, upgrade, restore, pilot, and reinstall screenshots into `artifacts/review/bg-009-<scenario>.png`.
- [ ] Record a 30 to 60 second video of an install, a sale, a day close, an upgrade, and a restore. Save it as `artifacts/review/bg-009-pilot.mp4`.
- [ ] Post the screenshots, the video, the exports, and the reconciliation summary for the operator. Stop at merge-ready and wait for the operator's click.

**Merge.**

- [ ] Record a clean release, recovery, security, hardware, pilot, and performance verdict at the exact BG-009 head SHA.
- [ ] Rebase onto current `main` after the verdict and prove that the patch content did not change.
- [ ] The operator squash-merges BG-009 only after the target-hardware pilot passes.

## Close the program

- [ ] Check every box with a file, log, screenshot, video, metric, test result, or SHA.
- [ ] Confirm that BG-P0 through BG-P5 and BG-009 are merged and that the ledger row holds a clean verdict for every merged head.
- [ ] Re-run the BG-009 pilot from current `main` on target hardware and compare its exports with the restored backup.
- [ ] Reconcile every agent as done, abandoned, or replaced. Record retries, partial results, and capability fallbacks.
- [ ] Audit the decision trail. Move recurring corrections into preferences, tests, analyzers, or scripts.
- [ ] Deliver the final queue table, merged PR links, SHAs, verdict summary, pilot bundle, open risks, state-store path, and decision-trail path to the operator.

## Appendix A. Verification discipline

For every PR before the push that faces origin, run these from the unit root.

```powershell
$env:Path = "C:\Users\nicky\.dotnet;$env:Path"
dotnet format --verify-no-changes
dotnet build -c Release
dotnet test -c Release
```

Pass a PR only when build reports zero errors and zero warnings, the full test run is green, and format reports zero diagnostics. Then run the live lanes.

```powershell
$env:BENTAGO_LIVE_LANES = "1"
$env:BENTAGO_HEAD = "<head-sha>"
```

## Appendix B. Evidence conventions

Unit evidence lands under `artifacts/verification/bg-pN/<BENTAGO_HEAD>/`. Operator review PNGs land in `artifacts/review/` as `bg-pN-<scenario>.png`. Review videos land in `artifacts/review/` as `bg-pN-<scope>.mp4`. Never commit a generated password, secret, customer data, or a raw database.

## Appendix C. One-writer and branch rules

The root checkout `C:\BentaGo` holds only operator and Codex edits and orchestrate edits. Never write code there. Work in an isolated worktree created from origin/main.

```powershell
git -C C:\BentaGo fetch origin main -q
git -C C:\BentaGo worktree add C:\BentaGo\.wt\<name> -b <branch> origin/main
```

One writer per worktree. Never rebase another writer's branch. Branches come from origin/main. One owner creates or changes EF migrations at a time. Open the PR ready, never draft. `gh` is at `C:\Program Files\GitHub CLI\gh.exe`. Repo `light691/bentaGo`. Required check is `build`.

## Appendix D. Standing delegation note

The operator squash-merges interaction-changing PRs after the review gate. The coordinator squash-merges units at merge-ready. Stop at a crucial gate: a palette or brand change, a scope change, a real-device pass, a review-gate click, or anything irreversible. Otherwise, merge and continue without waiting.
