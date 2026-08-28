# BentaGo program execution plan

BentaGo becomes an offline Windows POS for grocery route sales.
The program protects inventory and money with an append-only movement ledger, atomic business transactions, and auditable reversals.
Nine PRs deliver the app in dependency order from BG-001 through BG-009.
The operator reviews every interaction change and merges every PR.

## How to read this

One box is one unit of work. Every box names the evidence that checks it. A nested box is a sub-step of the box above it. Check a box only when its evidence exists, such as a file, log line, screenshot, test run, or SHA. The body is a how-to. The appendices explain and record.

The program runs the installed `$poteto-mode` `playbooks/orchestrate.md` workflow. The coordinator prepares merge-ready PRs. The operator reviews and merges BG-001 through BG-009.

Tests alone are not sufficient verification. A PR is verified only when its unit, live, and perf boxes are all checked.

## Program checklist

### Arm the program

- [ ] Present this plan and its nine PRs to the operator. Stop until the operator gives an explicit execution instruction.
- [ ] Resolve Gate 0 in `docs/00-pstack-style-master-plan.md`. Record accepted defaults or client answers in `docs/decisions/0001-mvp-operating-policy.md`.
- [ ] Add anonymized client CSV samples under `samples/input/`. Record the scanner model, the scanner suffix, the printer model, the printer interface, and the paper width in `docs/hardware-profile.md`.
- [ ] Install the .NET 10 SDK and the Windows desktop workload. Add `global.json` and prove that `dotnet --version` resolves the pinned SDK. The current host has only SDK 8.0.419.
- [ ] Establish the repository baseline. Create the first commit, set `main` as the default branch, and configure the remote only after the operator names the destination.
- [ ] On the operator's explicit execution instruction, create a goal with this exact text. "Run `docs/05-pstack-program-execution-plan.md` through BG-001 to BG-009. Verify every PR with unit, live, and perf evidence. Stop every interaction-changing PR at merge-ready for operator review. The program is done when BG-009 passes the pilot on target hardware and every earlier PR is merged."
- [ ] Read these files from trunk at program start. Re-read them at every tick.
  - [ ] `git show origin/main:docs/05-pstack-program-execution-plan.md`
  - [ ] `git show origin/main:docs/00-pstack-style-master-plan.md`
  - [ ] `git show origin/main:docs/01-product-requirements.md`
  - [ ] `git show origin/main:docs/02-technical-architecture-and-implementation-plan.md`
  - [ ] `git show origin/main:docs/03-ui-ux-keyboard-shortcuts-philippines.md`
  - [ ] `git show origin/main:docs/04-agent-delegation-contracts.md`
- [ ] Re-read the installed `$poteto-mode`, `$architect`, `$swarm`, `$interrogate`, `$no-comments`, and `$show-me-your-work` skills before the first dispatch.
- [ ] Create `orchestrate/bentago/` at the operator-approved state root. Initialize `preferences.md`, `overview.md`, `units.tsv`, `frontier.json`, `ledger.tsv`, `inbox/`, `gates.md`, `decisions.tsv`, and generated `status.md`.
- [ ] Arm a thread heartbeat for the 30-minute audit tick only after the operator authorizes autonomous execution.
- [ ] Use this tick prompt verbatim. "Re-read the execution playbook from trunk and the live goal. Audit the operation against both. Probe every active lane through supported status and judge progress by side effects only. Reconcile and stand down a stuck lane before one bounded replacement. Then send the operator the queue table, verdicts since the last tick, merges, open gates, and blockers."
- [ ] On the operator's hold or stand-down, send every owner a zero-writes order at once.

### Spawn owners

- [ ] Run BG-001 as the pilot. Do not fan out until its brief, verification recipe, and merge-ready handoff pass review.
- [ ] Spawn one owner per ready PR. Give each owner one worktree, one branch, one file boundary, and the full brief from `docs/04-agent-delegation-contracts.md`.
- [ ] Follow this dependency graph. BG-002 and BG-004 start after BG-001. BG-003 starts after BG-002. BG-005 starts after BG-003 and BG-004. BG-006 starts after BG-005. BG-007 starts after BG-006. BG-008 starts after BG-005, BG-006, and BG-007. BG-009 starts after BG-008.
- [ ] Keep one writer for `BentaGo.Core`, `BentaGo.Application`, EF migrations, shared WPF resources, and package version files.
- [ ] Give read-only exploration a shared checkout. Give every writer an isolated worktree.
- [ ] Hold the review gate. BG-001, BG-004, BG-005, BG-006, BG-007, BG-008, and BG-009 change an interaction. They wait for the operator's screenshot and video review before merge.
- [ ] Include the accepted Gate 0 decisions and upstream PR reports in every dependent brief. Refuse to dispatch a brief with an unresolved product rule.

### PR mechanics, for every PR

- [ ] Open the PR ready, never draft, with `gh pr create --draft=false` after the remote exists.
- [ ] Run `dotnet format --verify-no-changes`, `dotnet build -c Release`, and `dotnet test -c Release` before the PR-facing push. Push with hooks on.
- [ ] Run the installed cleanup checks before each commit. Run `$no-comments` before review.
- [ ] Triage every security or automated-review comment as fix, dismiss, or ask. Record the reason.
- [ ] Rebase onto current trunk before the merge-ready report. Do not let workers rebase another worker's branch.
- [ ] Keep migrations sequential. One owner creates or changes EF migrations at a time.
- [ ] Preserve receipts under `artifacts/verification/<pr-id>/<head-sha>/` and keep generated secrets, databases, and customer data out of Git.

### Verdict and merge, for every PR

- [ ] At the merge-ready head SHA, run the verification swarm. Use one gates lane, the ten live lanes from the PR's Verify, live block, the perf lane from its Verify, perf block, and one audit lane that distrusts the PR body.
- [ ] Mark the verdict clean only when every required lane is `PASS`. Send findings back to the owner. Treat a new head SHA as unverified.
- [ ] Record the PR number, head SHA, verifier identity, commands, screenshots, video, metric, baseline, and verdict in `ledger.tsv`.
- [ ] Stop interaction-changing PRs at merge-ready. The operator reviews their screenshots and video, then merges them.
- [ ] After each operator merge, rebuild from current `main`, rerun the smoke scenario, and update `frontier.json` before dispatching dependent work.

### Boot recipe, for every live lane

Each live lane runs in its own isolated environment at the PR head. WPF lanes use the installed Computer Use skill. Command and data-engine lanes use a fresh copied database and a dedicated output directory. A missing control capability blocks the lane.

- [ ] Run `git fetch origin <head-branch>` and check out the exact head SHA in the lane worktree.
- [ ] Run `dotnet restore`, `dotnet build -c Release`, and the PR-specific setup command.
- [ ] Copy the lane seed database to `artifacts/runtime/<pr-id>/lane-<n>/bentago.db`. Never share a writable database between lanes.
- [ ] Start BentaGo with the lane data directory. Wait for the **Ready** status and confirm that the process owns the expected database.
- [ ] Deliver keyboard, scanner, printer, and dialog input only through the named live-control path. Keep diagnostics read-only during observation.
- [ ] Save screenshots under `artifacts/verification/<pr-id>/<head-sha>/lane-<n>/` and return the paths with the lane report.

## Create the executable foundation (BG-001)

**Depends on.** None. Gate 0 answers, sample CSV files, hardware facts, .NET 10, an initial commit, and a remote are execution prerequisites.

**Files.**

- [ ] Create `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `BentaGo.sln`, and the six projects named in `docs/02-technical-architecture-and-implementation-plan.md`.
- [ ] Create `.editorconfig`, `.gitignore`, `.github/workflows/windows.yml`, `src/BentaGo.App/`, `src/BentaGo.Core/`, `src/BentaGo.Application/`, `src/BentaGo.Infrastructure/`, `tests/BentaGo.Tests/`, and `tools/BentaGo.Tools/`.
- [ ] Create the data-path, logging, configuration, dependency-injection, migration bootstrap, seed-admin, backup, and restore foundations.

**Build.**

- [ ] Pin .NET 10 and all NuGet versions. Start a 1366 by 768 WPF shell. Create a SQLite database under a configurable ProgramData path. Add a deterministic test data directory override.
- [ ] Add one command that creates, migrates, backs up, restores, and verifies a sample database without opening the full UI.

**You see.**

- [ ] A clean Windows account starts BentaGo, sees the login shell and **Ready**, closes it, restores a backup, and starts again with the seeded admin intact.

**Verify, unit.** Tests alone are not sufficient verification. A PR is verified only when its unit, live, and perf boxes are all checked.

- [ ] `tests/BentaGo.Tests/Foundation/BootstrapTests.cs` covers data paths, first migration, repeat migration, seed idempotency, backup, restore, and corrupt-backup rejection. Run `dotnet test -c Release --filter Foundation`.

**Verify, live.** Tests alone are not sufficient verification. A PR is verified only when its unit, live, and perf boxes are all checked. Ten lanes on the configured fast profile at the PR head use Computer Use or the foundation command.

- [ ] Lane 1. Start with no data directory. Save `bg-001-first-start.png`. Pass when the login shell shows **Ready** and the database exists.
- [ ] Lane 2. Start a second time. Save `bg-001-repeat-start.png`. Pass when no duplicate admin or migration appears.
- [ ] Lane 3. Start at 1366 by 768. Save `bg-001-1366-layout.png`. Pass when the shell has no clipped navigation or status text.
- [ ] Lane 4. Start at 1920 by 1080. Save `bg-001-1080-layout.png`. Pass when the shell remains aligned and usable.
- [ ] Lane 5. Create a backup. Save `bg-001-backup-created.png`. Pass when the backup file and success receipt name the same database version.
- [ ] Lane 6. Restore the backup. Save `bg-001-restore.png`. Pass when the seeded admin survives and the pre-restore database is preserved.
- [ ] Lane 7. Select a corrupt backup. Save `bg-001-corrupt-backup.png`. Pass when restore refuses the file without changing the active database.
- [ ] Lane 8. Make the data folder read-only. Save `bg-001-data-path-error.png`. Pass when BentaGo explains the path failure and exits without partial files.
- [ ] Lane 9. Run migration twice. Save `bg-001-repeat-migration.png`. Pass when both runs converge to the same schema.
- [ ] Lane 10. Close and restart after an unclean process stop. Save `bg-001-recovery-start.png`. Pass when BentaGo opens the intact database or reports a concrete recovery error.

**Verify, perf.** Tests alone are not sufficient verification. A PR is verified only when its unit, live, and perf boxes are all checked.

- [ ] Metric. Cold start from process launch to **Ready**.
- [ ] Probe. Measure ten clean starts on the pinned host with a warm-up run excluded.
- [ ] Baseline. Record the first accepted BG-001 median as the program startup baseline.
- [ ] Rule. Fail BG-001 when the median exceeds five seconds or any run exceeds eight seconds.

**Review gate.** The operator reviews before merge.

- [ ] Copy the 1366 and 1080 screenshots into `artifacts/review/bg-001-shell.png` and `artifacts/review/bg-001-shell-1080.png`.
- [ ] Record a 30 to 60 second video of first start, backup, restore, and restart. Save it as `artifacts/review/bg-001-shell.mp4`.
- [ ] Post the screenshots and video for the operator. Stop at merge-ready and wait for the operator's click.

**Merge.**

- [ ] Record a clean root verdict at the exact BG-001 head SHA.
- [ ] Rebase onto current `main` after the verdict and prove that the patch content did not change.
- [ ] The operator squash-merges BG-001 after the review gate passes.

## Model the trip, money, and inventory rules (BG-002)

**Depends on.** BG-001.

**Files.**

- [ ] Create domain models under `src/BentaGo.Core/Domain/` and public application contracts under `src/BentaGo.Application/Contracts/`.
- [ ] Create scenario builders and invariant tests under `tests/BentaGo.Tests/Domain/`.

**Build.**

- [ ] Model `Money`, `Quantity`, IDs, inventory locations, movements, trips, sales, payments, returns, adjustments, counts, closes, and audit events as types that reject illegal states.
- [ ] Encode trip and document transitions explicitly. Keep EF, WPF, CSV, printer, and filesystem types outside the domain.

**You see.**

- [ ] `BentaGo.Tools scenario two-peddlers` prints the complete ordered business event sequence and exact expected cash and stock balances without using SQLite.

**Verify, unit.** Tests alone are not sufficient verification. A PR is verified only when its unit, live, and perf boxes are all checked.

- [ ] `tests/BentaGo.Tests/Domain/` covers every state transition, payment-total invariant, return limit, reversal reference, stock ownership rule, centavo calculation, and Manila display conversion. Run `dotnet test -c Release --filter Domain`.

**Verify, live.** Tests alone are not sufficient verification. A PR is verified only when its unit, live, and perf boxes are all checked. Ten lanes on the configured fast profile at the PR head use `BentaGo.Tools` and isolated outputs.

- [ ] Lane 1. Run a cash sale. Save `bg-002-cash-sale.png`. Pass when stock and expected cash move by exact centavos.
- [ ] Lane 2. Run GCash, Maya, and bank-transfer sales. Save `bg-002-noncash.png`. Pass when revenue rises and physical expected cash does not.
- [ ] Lane 3. Run a credit sale. Save `bg-002-credit.png`. Pass when the required reference exists and cash remittance excludes the sale.
- [ ] Lane 4. Run a split payment. Save `bg-002-split.png`. Pass when the payment sum equals the sale total exactly.
- [ ] Lane 5. Try an underpaid split. Save `bg-002-underpaid.png`. Pass when construction fails before any event exists.
- [ ] Lane 6. Sell from the wrong peddler location. Save `bg-002-location-isolation.png`. Pass when the domain rejects the sale.
- [ ] Lane 7. Void a sale. Save `bg-002-void.png`. Pass when the reversal points to the original and preserves both records.
- [ ] Lane 8. Partially return a line twice. Save `bg-002-return-limit.png`. Pass when cumulative return cannot exceed sold quantity.
- [ ] Lane 9. Close and then mutate a trip. Save `bg-002-closed-trip.png`. Pass when the mutation fails until an authorized reopen event exists.
- [ ] Lane 10. Re-run the full two-peddler scenario. Save `bg-002-repeat-scenario.png`. Pass when both outputs are byte-identical.

**Verify, perf.** Tests alone are not sufficient verification. A PR is verified only when its unit, live, and perf boxes are all checked.

- [ ] Metric. Time to evaluate 100,000 inventory movements into balances.
- [ ] Probe. Run the deterministic domain benchmark five times after one warm-up.
- [ ] Baseline. Record the median on the pinned host before persistence code exists.
- [ ] Rule. Fail when the median exceeds one second or allocations exceed the recorded budget by 10 percent.

**Review gate.** None. BG-002 is not review-gated because it changes no interaction.

**Merge.**

- [ ] Record a clean independent architecture and invariant verdict at the exact BG-002 head SHA.
- [ ] Rebase onto current `main` after the verdict and prove that the patch content did not change.
- [ ] The operator squash-merges BG-002 after the domain review passes.

## Persist the ledger and atomic operations (BG-003)

**Depends on.** BG-002.

**Files.**

- [ ] Create EF Core mappings, migrations, SQLite services, transaction handlers, projections, and backup integration under `src/BentaGo.Infrastructure/`.
- [ ] Create application use cases and SQLite integration tests under `src/BentaGo.Application/` and `tests/BentaGo.Tests/Persistence/`.

**Build.**

- [ ] Persist the append-only movement ledger. Rebuild balances from movements. Implement atomic stock load, checkout, reversal, return, adjustment, close, and reopen operations.
- [ ] Add stable idempotency keys and unique business references. Commit sales before printing and keep print state outside the sale transaction.

**You see.**

- [ ] The persisted two-peddler scenario survives restart, rebuilds the same balances from movements, and explains every cash and stock variance.

**Verify, unit.** Tests alone are not sufficient verification. A PR is verified only when its unit, live, and perf boxes are all checked.

- [ ] `tests/BentaGo.Tests/Persistence/` uses real SQLite files for transaction rollback, concurrent checkout, unique keys, ledger rebuild, close lock, migration, backup, and restore. Run `dotnet test -c Release --filter Persistence`.

**Verify, live.** Tests alone are not sufficient verification. A PR is verified only when its unit, live, and perf boxes are all checked. Ten lanes on the configured fast profile at the PR head use `BentaGo.Tools` and fresh SQLite files.

- [ ] Lane 1. Commit a stock load. Save `bg-003-stock-load.png`. Pass when both locations and every movement reconcile.
- [ ] Lane 2. Commit a sale. Save `bg-003-checkout.png`. Pass when sale, lines, payments, movements, balance, and audit commit together.
- [ ] Lane 3. Inject failure before commit. Save `bg-003-rollback.png`. Pass when no partial sale state exists.
- [ ] Lane 4. Submit the same checkout key twice. Save `bg-003-idempotency.png`. Pass when one sale exists and both requests return the same receipt reference.
- [ ] Lane 5. Submit two competing stock sales. Save `bg-003-concurrency.png`. Pass when committed stock never goes below zero.
- [ ] Lane 6. Rebuild all balances. Save `bg-003-rebuild.png`. Pass when rebuilt and cached balances match exactly.
- [ ] Lane 7. Void and return persisted sales. Save `bg-003-reversals.png`. Pass when original rows remain and reversing movements reconcile.
- [ ] Lane 8. Restart after forced termination. Save `bg-003-power-loss.png`. Pass when SQLite recovery yields either the full transaction or none of it.
- [ ] Lane 9. Upgrade a prior schema fixture. Save `bg-003-migration.png`. Pass when migration preserves ledger totals and creates a backup first.
- [ ] Lane 10. Restore the upgraded backup. Save `bg-003-restore.png`. Pass when reports match the pre-upgrade fixture.

**Verify, perf.** Tests alone are not sufficient verification. A PR is verified only when its unit, live, and perf boxes are all checked.

- [ ] Metric. P95 SQLite checkout transaction time with 10,000 products and 100,000 movements.
- [ ] Probe. Interleave 100 checkout runs against trunk and the BG-003 head on the pinned host.
- [ ] Baseline. Record the trunk empty-transaction and BG-002 in-memory scenario times first.
- [ ] Rule. Fail when P95 checkout exceeds 500 milliseconds or ledger rebuild exceeds two seconds.

**Review gate.** None. BG-003 is not review-gated because it changes no interaction.

**Merge.**

- [ ] Record clean independent ledger, transaction, migration, and recovery verdicts at the exact BG-003 head SHA.
- [ ] Rebase onto current `main` after the verdict and prove that the patch content did not change.
- [ ] The operator squash-merges BG-003 after the high-risk review passes.

## Build the WPF shell and design system (BG-004)

**Depends on.** BG-001. Consume BG-002 contracts after BG-002 merges.

**Files.**

- [ ] Create WPF resources, shell views, shell view models, navigation, commands, dialog service, validation templates, and keyboard help under `src/BentaGo.App/`.
- [ ] Create view-model and accessibility tests under `tests/BentaGo.Tests/App/`.

**Build.**

- [ ] Implement the six WPF reference screens with placeholder or stable contract data from `designs/bentago-wpf-mvp-screen-set.png`.
- [ ] Standardize shortcuts on `docs/03-ui-ux-keyboard-shortcuts-philippines.md`. Use F12 for checkout and F10 for end trip. Treat conflicting labels in the image as visual reference text, not behavior.

**You see.**

- [ ] A keyboard-only user moves through login, peddler home, sales, payment, stock, and reconciliation at both target resolutions with visible focus and no business logic in code-behind.

**Verify, unit.** Tests alone are not sufficient verification. A PR is verified only when its unit, live, and perf boxes are all checked.

- [ ] `tests/BentaGo.Tests/App/` covers navigation, command enablement, focus restoration requests, shortcut conflicts, validation state, dialog results, and view-model disposal. Run `dotnet test -c Release --filter AppShell`.

**Verify, live.** Tests alone are not sufficient verification. A PR is verified only when its unit, live, and perf boxes are all checked. Ten lanes on the configured fast profile at the PR head use Computer Use.

- [ ] Lane 1. Open login at 1366 by 768. Save `bg-004-login-1366.png`. Pass when fields, action, status, and version are visible without scrolling.
- [ ] Lane 2. Open login at 1920 by 1080. Save `bg-004-login-1080.png`. Pass when spacing remains balanced and controls do not stretch excessively.
- [ ] Lane 3. Navigate with Alt shortcuts. Save `bg-004-alt-navigation.png`. Pass when each destination opens once and focus lands on its primary control.
- [ ] Lane 4. Open F1 help. Save `bg-004-shortcuts.png`. Pass when every documented shortcut appears and conflicts are absent.
- [ ] Lane 5. Tab through login. Save `bg-004-login-focus.png`. Pass when focus order is logical and visible.
- [ ] Lane 6. Open and cancel a dialog. Save `bg-004-dialog.png`. Pass when Esc closes only the top dialog and restores prior focus.
- [ ] Lane 7. Trigger field validation. Save `bg-004-validation.png`. Pass when color and text identify the field and the corrective action.
- [ ] Lane 8. Switch between all six screens. Save `bg-004-navigation-state.png`. Pass when navigation does not duplicate views or retain stale dialogs.
- [ ] Lane 9. Use Windows high contrast. Save `bg-004-high-contrast.png`. Pass when focus, status, and actions remain distinguishable.
- [ ] Lane 10. Run at 125 percent display scaling. Save `bg-004-scaling.png`. Pass when no critical control clips or overlaps.

**Verify, perf.** Tests alone are not sufficient verification. A PR is verified only when its unit, live, and perf boxes are all checked.

- [ ] Metric. P95 navigation time from command to rendered destination.
- [ ] Probe. Measure 100 navigation changes after startup at both target resolutions.
- [ ] Baseline. Record the BG-001 shell navigation time first.
- [ ] Rule. Fail when P95 exceeds 150 milliseconds or managed memory grows by more than 20 MB after 500 changes.

**Review gate.** The operator reviews before merge.

- [ ] Copy screenshots for all six screens at 1366 by 768 into `artifacts/review/bg-004-<screen>.png`.
- [ ] Record a 30 to 60 second keyboard-only navigation and focus video. Save it as `artifacts/review/bg-004-navigation.mp4`.
- [ ] Post the screenshots and video for the operator. Stop at merge-ready and wait for the operator's click.

**Merge.**

- [ ] Record clean accessibility, layout, navigation, and code-boundary verdicts at the exact BG-004 head SHA.
- [ ] Rebase onto current `main` after the verdict and prove that the patch content did not change.
- [ ] The operator squash-merges BG-004 after the review gate passes.

## Add catalog, identity, trips, stock loads, and CSV exchange (BG-005)

**Depends on.** BG-003 and BG-004.

**Files.**

- [ ] Create authentication, roles, catalog, peddler, trip, stock-load, import, export, and import-history use cases and infrastructure.
- [ ] Create the related admin views and view models under `src/BentaGo.App/Features/Admin/` and fixtures under `samples/`.

**Build.**

- [ ] Parse CSV into typed preview rows at the boundary. Preserve leading zeros. Commit accepted batches atomically and retain immutable import receipts and understandable error files.
- [ ] Implement local password hashing, role checks, user and peddler separation, trip creation, load approval, and isolated inventory views.

**You see.**

- [ ] An admin imports products, creates two peddlers and trips, loads different stock to each, and proves that each peddler sees only assigned inventory.

**Verify, unit.** Tests alone are not sufficient verification. A PR is verified only when its unit, live, and perf boxes are all checked.

- [ ] Tests cover password verification, lockout policy, permissions, CSV encoding and delimiter detection, leading zeros, duplicate keys, atomic commit, import history, stock transfer, and peddler isolation. Run `dotnet test -c Release --filter "Auth|Catalog|Import|StockLoad"`.

**Verify, live.** Tests alone are not sufficient verification. A PR is verified only when its unit, live, and perf boxes are all checked. Ten lanes on the configured fast profile at the PR head use Computer Use.

- [ ] Lane 1. Log in as admin. Save `bg-005-admin-login.png`. Pass when the admin sees only authorized navigation and a successful audit event.
- [ ] Lane 2. Fail login without revealing account existence. Save `bg-005-login-failure.png`. Pass when the message is safe and the attempt is audited.
- [ ] Lane 3. Preview a valid product CSV. Save `bg-005-product-preview.png`. Pass when counts and representative rows match the file before commit.
- [ ] Lane 4. Preview an invalid product CSV. Save `bg-005-product-errors.png`. Pass when row errors are specific and the database remains unchanged.
- [ ] Lane 5. Import barcodes with leading zeros. Save `bg-005-leading-zeros.png`. Pass when search and export retain every digit.
- [ ] Lane 6. Create two peddlers and trips. Save `bg-005-two-peddlers.png`. Pass when each has one active trip and a separate location.
- [ ] Lane 7. Preview and approve a stock load. Save `bg-005-stock-load.png`. Pass when source availability and destination quantities are visible before commit.
- [ ] Lane 8. Submit a load with insufficient stock. Save `bg-005-load-rejected.png`. Pass when no movement commits and the shortage is clear.
- [ ] Lane 9. Export imported products and loads. Save `bg-005-export.png`. Pass when stable IDs, references, totals, and encoding match the contract.
- [ ] Lane 10. Log in as peddler. Save `bg-005-peddler-stock.png`. Pass when the peddler sees only assigned stock and no admin action.

**Verify, perf.** Tests alone are not sufficient verification. A PR is verified only when its unit, live, and perf boxes are all checked.

- [ ] Metric. Preview and atomic commit time for 50,000 product rows.
- [ ] Probe. Interleave five imports against trunk fixtures and the BG-005 head with a fresh database for each run.
- [ ] Baseline. Record parser-only and empty-commit times on current `main` first.
- [ ] Rule. Fail when preview exceeds three seconds, commit exceeds five seconds, or UI input stalls for more than 500 milliseconds.

**Review gate.** The operator reviews before merge.

- [ ] Copy login, import preview, import error, stock load, and peddler stock screenshots into `artifacts/review/bg-005-<screen>.png`.
- [ ] Record a 30 to 60 second video of product import and a two-peddler stock load. Save it as `artifacts/review/bg-005-admin.mp4`.
- [ ] Post the screenshots and video for the operator. Stop at merge-ready and wait for the operator's click.

**Merge.**

- [ ] Record clean security, import, transaction, permission, and UI verdicts at the exact BG-005 head SHA.
- [ ] Rebase onto current `main` after the verdict and prove that the patch content did not change.
- [ ] The operator squash-merges BG-005 after the review gate passes.

## Complete offline sales and payments (BG-006)

**Depends on.** BG-005.

**Files.**

- [ ] Create product search, scanner input, cart, discount, payment, checkout, receipt-result, and transaction-history features.
- [ ] Create checkout integration tests and representative seeded sales under `tests/BentaGo.Tests/Sales/`.

**Build.**

- [ ] Implement keyboard-HID scan handling, manual search, cart commands, allowed discounts, Cash, GCash, Maya, Bank Transfer, Credit, split payments, and idempotent checkout.
- [ ] Restore scanner focus after every completed scan. Disable duplicate checkout while the command runs. Show a saved-sale state before any print attempt.

**You see.**

- [ ] A peddler completes the full offline sales set with a keyboard and scanner. Every sale updates stock, payments, movements, and audit once.

**Verify, unit.** Tests alone are not sufficient verification. A PR is verified only when its unit, live, and perf boxes are all checked.

- [ ] Tests cover product lookup, repeated scans, cart quantity, stock limits, discounts, cash change, every payment method, split totals, credit requirements, idempotency, duplicate Enter, and failed persistence. Run `dotnet test -c Release --filter Sales`.

**Verify, live.** Tests alone are not sufficient verification. A PR is verified only when its unit, live, and perf boxes are all checked. Ten lanes on the configured fast profile at the PR head use Computer Use and keyboard-HID input.

- [ ] Lane 1. Scan one product twice. Save `bg-006-repeat-scan.png`. Pass when one line has quantity two and focus returns to scan input.
- [ ] Lane 2. Search by SKU, barcode, and name. Save `bg-006-search.png`. Pass when all three find the same product and show current stock.
- [ ] Lane 3. Complete a cash sale. Save `bg-006-cash.png`. Pass when change, receipt number, stock, and expected cash are exact.
- [ ] Lane 4. Complete GCash and Maya sales. Save `bg-006-wallets.png`. Pass when references persist and physical cash excludes both totals.
- [ ] Lane 5. Complete a bank-transfer sale. Save `bg-006-bank.png`. Pass when the reference is required and searchable.
- [ ] Lane 6. Complete a credit sale. Save `bg-006-credit.png`. Pass when approved customer fields persist and cash remittance excludes the total.
- [ ] Lane 7. Complete a split payment. Save `bg-006-split.png`. Pass when completion stays disabled until the balance is zero.
- [ ] Lane 8. Press Enter twice during checkout. Save `bg-006-double-submit.png`. Pass when one sale and one receipt number exist.
- [ ] Lane 9. Try to oversell stock. Save `bg-006-oversell.png`. Pass when checkout refuses before persistence and keeps the cart recoverable.
- [ ] Lane 10. Restart offline and open transaction history. Save `bg-006-offline-history.png`. Pass when all committed sales and payment details remain available.

**Verify, perf.** Tests alone are not sufficient verification. A PR is verified only when its unit, live, and perf boxes are all checked.

- [ ] Metric. Product lookup latency and checkout completion latency on target data volume.
- [ ] Probe. Measure 1,000 lookups and 100 checkouts with 50,000 products and 100,000 movements.
- [ ] Baseline. Record BG-005 product search and BG-003 transaction medians first.
- [ ] Rule. Fail when P95 lookup exceeds 100 milliseconds or P95 checkout exceeds two seconds.

**Review gate.** The operator reviews before merge.

- [ ] Copy sale, payment, split, error, and result screenshots into `artifacts/review/bg-006-<scenario>.png`.
- [ ] Record a 30 to 60 second keyboard-only sale from scan through saved result. Save it as `artifacts/review/bg-006-sale.mp4`.
- [ ] Post the screenshots and video for the operator. Stop at merge-ready and wait for the operator's click.

**Merge.**

- [ ] Record clean sales, payment, stock, idempotency, focus, and performance verdicts at the exact BG-006 head SHA.
- [ ] Rebase onto current `main` after the verdict and prove that the patch content did not change.
- [ ] The operator squash-merges BG-006 after the review gate passes.

## Add receipts, hardware diagnostics, and corrections (BG-007)

**Depends on.** BG-006.

**Files.**

- [ ] Create receipt models, ESC/POS and fake printer adapters, print job persistence, scanner diagnostics, and printer settings.
- [ ] Create void, return, damage, loss, adjustment, approval, and audit views and use cases.

**Build.**

- [ ] Render receipts from a stored model. Print only after checkout commits. Make retry and reprint idempotent and label every reprint.
- [ ] Implement corrections as referenced documents and movements. Require the accepted role, reason, and approval policy.

**You see.**

- [ ] An unplugged printer leaves one saved sale and one recoverable print job. Corrections preserve original records and reconcile inventory.

**Verify, unit.** Tests alone are not sufficient verification. A PR is verified only when its unit, live, and perf boxes are all checked.

- [ ] Tests cover receipt rendering, ESC/POS bytes, fake adapter failures, retry, reprint labels, scanner suffixes, void authority, partial returns, return destinations, damage, loss, adjustment approval, and audit events. Run `dotnet test -c Release --filter "Printer|Scanner|Corrections"`.

**Verify, live.** Tests alone are not sufficient verification. A PR is verified only when its unit, live, and perf boxes are all checked. Ten lanes on the configured fast profile at the PR head use Computer Use and the real devices when available.

- [ ] Lane 1. Print a test receipt. Save `bg-007-test-receipt.png`. Pass when status, width, encoding, and cut behavior match the hardware profile.
- [ ] Lane 2. Unplug before checkout. Save `bg-007-unplugged.png`. Pass when the sale is saved once and the UI offers reprint.
- [ ] Lane 3. Reconnect and retry. Save `bg-007-retry.png`. Pass when the original sale prints without another sale or movement.
- [ ] Lane 4. Reprint a successful receipt. Save `bg-007-reprint.png`. Pass when the copy says **REPRINT** and keeps the original receipt number.
- [ ] Lane 5. Run scanner diagnostics. Save `bg-007-scanner.png`. Pass when raw input, suffix, and matched product are visible.
- [ ] Lane 6. Void an open-trip sale. Save `bg-007-void.png`. Pass when approval and reason are required and reversing movements reconcile.
- [ ] Lane 7. Return part of a sale to peddler stock. Save `bg-007-return-stock.png`. Pass when returnable quantity and restored stock are correct.
- [ ] Lane 8. Return an item to main store. Save `bg-007-return-store.png`. Pass when both locations and the reference reconcile.
- [ ] Lane 9. Record damage and loss. Save `bg-007-damage-loss.png`. Pass when each reason, actor, timestamp, and movement appears.
- [ ] Lane 10. Attempt a correction after close. Save `bg-007-closed-correction.png`. Pass when the action requires authorized reopen and preserves the audit sequence.

**Verify, perf.** Tests alone are not sufficient verification. A PR is verified only when its unit, live, and perf boxes are all checked.

- [ ] Metric. Time from print command to first printer write and time to render a 100-line receipt.
- [ ] Probe. Measure 30 fake-adapter and 10 real-device runs after one warm-up.
- [ ] Baseline. Record fake-adapter transport and plain-text render times before the ESC/POS adapter runs.
- [ ] Rule. Fail when P95 first write exceeds one second or a 100-line render exceeds 200 milliseconds.

**Review gate.** The operator reviews before merge.

- [ ] Copy saved-sale, retry, reprint, scanner, void, return, and adjustment screenshots into `artifacts/review/bg-007-<scenario>.png`.
- [ ] Record a 30 to 60 second unplug, reconnect, and reprint video. Save it as `artifacts/review/bg-007-hardware.mp4`.
- [ ] Post the screenshots and video for the operator. Stop at merge-ready and wait for the operator's click.

**Merge.**

- [ ] Record clean device, idempotency, correction, permission, and audit verdicts at the exact BG-007 head SHA.
- [ ] Rebase onto current `main` after the verdict and prove that the patch content did not change.
- [ ] The operator squash-merges BG-007 after the review gate passes.

## Reconcile trips and produce reports (BG-008)

**Depends on.** BG-005, BG-006, and BG-007.

**Files.**

- [ ] Create cash close, physical count, variance, submit, close, reopen, report, print summary, and CSV export features.
- [ ] Create golden report fixtures and the two-peddler end-to-end scenario under `tests/BentaGo.Tests/Reconciliation/`.

**Build.**

- [ ] Calculate expected cash from payment lines and expected stock from movements. Preserve submitted counts and remittance as separate facts.
- [ ] Lock closed trips. Reopen only through authorized audited action. Generate reports that explain every difference without direct database access.

**You see.**

- [ ] The operator closes one balanced trip and one trip with intentional cash and stock variances, then explains every result from the screen and exports.

**Verify, unit.** Tests alone are not sufficient verification. A PR is verified only when its unit, live, and perf boxes are all checked.

- [ ] Tests cover payment summaries, refunds, credit exclusion, cash adjustments, count variance, balanced, shortage, overage, close lock, reopen, report totals, CSV escaping, and timezone boundaries. Run `dotnet test -c Release --filter Reconciliation`.

**Verify, live.** Tests alone are not sufficient verification. A PR is verified only when its unit, live, and perf boxes are all checked. Ten lanes on the configured fast profile at the PR head use Computer Use.

- [ ] Lane 1. Open a balanced close. Save `bg-008-balanced.png`. Pass when expected and actual cash and stock match and the screen says **BALANCED**.
- [ ] Lane 2. Enter a cash shortage. Save `bg-008-cash-short.png`. Pass when amount, sign, label, and required note are correct.
- [ ] Lane 3. Enter a cash overage. Save `bg-008-cash-over.png`. Pass when amount, sign, label, and required note are correct.
- [ ] Lane 4. Enter stock shortages and overages. Save `bg-008-stock-variance.png`. Pass when every line shows expected, counted, quantity variance, and value.
- [ ] Lane 5. Save and reopen a draft. Save `bg-008-close-draft.png`. Pass when no trip lock or final audit event exists.
- [ ] Lane 6. Submit and close. Save `bg-008-close-lock.png`. Pass when sales and corrections lock and the close receipt persists.
- [ ] Lane 7. Reopen with admin approval. Save `bg-008-reopen.png`. Pass when the reason and actor appear and the prior close remains visible.
- [ ] Lane 8. Export transaction and reconciliation CSV files. Save `bg-008-exports.png`. Pass when report totals equal the screen and ledger.
- [ ] Lane 9. Print the close summary. Save `bg-008-close-print.png`. Pass when payment and variance totals fit the configured paper width.
- [ ] Lane 10. Reconcile the complete two-peddler scenario. Save `bg-008-two-peddlers.png`. Pass when a reviewer explains every difference without reading SQLite.

**Verify, perf.** Tests alone are not sufficient verification. A PR is verified only when its unit, live, and perf boxes are all checked.

- [ ] Metric. Time to calculate and render a close with 10,000 sales and 50,000 movements.
- [ ] Probe. Interleave ten close and report runs against trunk and the BG-008 head.
- [ ] Baseline. Record BG-003 ledger aggregation and BG-006 history load medians first.
- [ ] Rule. Fail when P95 close calculation exceeds one second or report rendering exceeds two seconds.

**Review gate.** The operator reviews before merge.

- [ ] Copy balanced, shortage, overage, count, lock, reopen, and export screenshots into `artifacts/review/bg-008-<scenario>.png`.
- [ ] Record a 30 to 60 second close, export, and reopen video. Save it as `artifacts/review/bg-008-reconciliation.mp4`.
- [ ] Post the screenshots and video for the operator. Stop at merge-ready and wait for the operator's click.

**Merge.**

- [ ] Record clean accounting, inventory, close-lock, report, export, and UI verdicts at the exact BG-008 head SHA.
- [ ] Rebase onto current `main` after the verdict and prove that the patch content did not change.
- [ ] The operator squash-merges BG-008 after the review gate passes.

## Package, recover, and pass the pilot (BG-009)

**Depends on.** BG-008.

**Files.**

- [ ] Create self-contained publish settings, Velopack configuration, installer assets, update tests, support bundle, pilot script, and operations guide.
- [ ] Create hardening fixtures for upgrade, power interruption, large data, locked files, corrupt files, and backup restore.

**Build.**

- [ ] Package a Windows x64 installer. Keep writable data under ProgramData. Run update bootstrap before WPF startup. Preserve and verify data across upgrade and uninstall choices.
- [ ] Automate the full two-peddler pilot and collect a signed evidence bundle with database hash, exports, screenshots, logs, backup, and restore report.

**You see.**

- [ ] A clean target laptop installs, runs fully offline, completes the pilot, upgrades, restores from backup, and produces the same reports.

**Verify, unit.** Tests alone are not sufficient verification. A PR is verified only when its unit, live, and perf boxes are all checked.

- [ ] Release tests cover publish layout, data-path separation, update bootstrap, migration backup, support-bundle redaction, uninstall preservation, and pilot assertions. Run `dotnet test -c Release --filter "Release|Pilot"`.

**Verify, live.** Tests alone are not sufficient verification. A PR is verified only when its unit, live, and perf boxes are all checked. Ten lanes on the configured fast profile at the PR head use Computer Use on clean or restored Windows environments.

- [ ] Lane 1. Install on a clean Windows account. Save `bg-009-clean-install.png`. Pass when BentaGo starts without an SDK or administrator session.
- [ ] Lane 2. Complete first-run setup. Save `bg-009-first-run.png`. Pass when data, logs, backup, and settings use the approved ProgramData folders.
- [ ] Lane 3. Work fully offline. Save `bg-009-offline.png`. Pass when login, sale, print, close, report, and backup never wait for internet.
- [ ] Lane 4. Upgrade an existing installation. Save `bg-009-upgrade.png`. Pass when data and settings survive and migration runs once.
- [ ] Lane 5. Interrupt upgrade and restart. Save `bg-009-upgrade-recovery.png`. Pass when the prior or new version starts with valid data.
- [ ] Lane 6. Restore the pre-upgrade backup. Save `bg-009-restore.png`. Pass when the same reports and ledger totals return.
- [ ] Lane 7. Run with a locked import file and locked backup target. Save `bg-009-locked-files.png`. Pass when messages name the blocked file and no partial output remains.
- [ ] Lane 8. Run the full two-peddler pilot. Save `bg-009-pilot.png`. Pass when all eight outcomes in `docs/00-pstack-style-master-plan.md` succeed.
- [ ] Lane 9. Generate a support bundle. Save `bg-009-support-bundle.png`. Pass when logs and metadata exist and passwords, PINs, customer details, and raw database content do not.
- [ ] Lane 10. Uninstall and reinstall. Save `bg-009-reinstall.png`. Pass when the approved data-retention choice is honored and the restored app reconciles.

**Verify, perf.** Tests alone are not sufficient verification. A PR is verified only when its unit, live, and perf boxes are all checked.

- [ ] Metric. Cold start, checkout P95, import time, close time, installer time, and peak working set on target hardware.
- [ ] Probe. Run the published self-contained build three times before and after packaging against the pilot dataset.
- [ ] Baseline. Use the accepted BG-001, BG-005, BG-006, and BG-008 metrics at their merged SHAs.
- [ ] Rule. Fail when startup exceeds five seconds, checkout exceeds two seconds, any metric regresses by more than 10 percent, or peak working set exceeds the approved target-hardware budget.

**Review gate.** The operator reviews before merge.

- [ ] Copy install, offline, upgrade, restore, pilot, and reinstall screenshots into `artifacts/review/bg-009-<scenario>.png`.
- [ ] Record a 30 to 60 second install, sale, close, upgrade, and restore summary video. Save it as `artifacts/review/bg-009-pilot.mp4`.
- [ ] Post the screenshots, video, exports, and reconciliation summary for the operator. Stop at merge-ready and wait for the operator's click.

**Merge.**

- [ ] Record clean release, recovery, security, hardware, pilot, and performance verdicts at the exact BG-009 head SHA.
- [ ] Rebase onto current `main` after the verdict and prove that the patch content did not change.
- [ ] The operator squash-merges BG-009 only after the target-hardware pilot passes.

## Close the program

- [ ] Check every box above with a file, log, screenshot, video, metric, test result, or SHA.
- [ ] Confirm that BG-001 through BG-009 are merged and that `ledger.tsv` holds a clean verdict for every merged head.
- [ ] Re-run the BG-009 pilot from current `main` on target hardware and compare its exports with the restored backup.
- [ ] Reconcile every agent as done, abandoned, or replaced. Record retries, partial results, and capability fallbacks.
- [ ] Audit `decisions.tsv`. Move recurring corrections into `preferences.md`, tests, analyzers, or scripts.
- [ ] Deliver the final queue table, merged PR links, SHAs, verdict summary, pilot bundle, open risks, state-store path, and decision-trail path to the operator.

## Appendix A. Prototype evidence

The selected reference is `designs/bentago-wpf-mvp-screen-set.png`. It matches the locked WPF stack and the information density needed at 1366 by 768.

The rejected reference is `designs/bentago-mvp-screen-set.png`. It implies online synchronization, uses a conflicting shortcut set, and presents a web-like card layout that does not match the offline-only MVP.

The static reference does not prove keyboard focus, scanner suffix behavior, high-contrast behavior, display scaling, dialog focus restoration, or real printer output. BG-004 and BG-007 prove those behaviors before later work depends on them.

## Appendix B. Alternatives rejected

One large implementation branch was rejected. It would hide the first failing boundary and make independent verification weak.

A web client and local API were rejected. The MVP is one Windows WPF process and does not need a network boundary.

Snapshot-only inventory was rejected. It cannot explain differences after returns, damage, loss, voids, and retries.

Printing inside checkout was rejected. A device failure must not roll back or duplicate a committed sale.

Parallel migration owners were rejected. EF migrations and shared domain contracts require one writer.

## Appendix C. Risks

Gate 0 is open. Tax status, authoritative product identity, sample CSV formats, discount authority, credit policy, expense policy, count cadence, return rules, void authority, hardware, and user policy still need accepted defaults or client answers before BG-001 dispatch.

The repository has no commits and no configured remote. PR work cannot start until the operator names a destination and authorizes the initial baseline.

The host has .NET SDK 8.0.419 only. BG-001 requires .NET 10 and a pinned `global.json`.

Real printer and scanner models are unknown. BG-007 fails closed on device claims until the hardware profile and devices exist.

The main POS CSV contract is unknown. BG-005 uses versioned fixtures first, but client samples remain a release gate.

Credit and formal receipt rules may carry legal or accounting requirements. Accepted client policy controls scope. The software must not invent tax fields or a receivables ledger.

Computer Use is available for Windows UI verification. If it cannot control the target WPF build, every affected live lane stays failed until the control path works.

## Appendix D. Links and reading list

Read `README.md` and `docs/00-pstack-style-master-plan.md` through `docs/04-agent-delegation-contracts.md` before editing.

Use the installed `$poteto-mode` `playbooks/orchestrate.md` workflow for execution. Use `$architect` before changing domain or cross-project contracts. Use `$swarm` for the merge-ready verdict. Use `$interrogate` for BG-002, BG-003, BG-006, BG-007, and BG-008.

Use Microsoft's WPF guide for .NET 10 at <https://learn.microsoft.com/en-us/dotnet/desktop/wpf/get-started/create-app-visual-studio>. Use Microsoft's EF Core migration guidance at <https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/managing>. Use Velopack's WPF guide at <https://docs.velopack.io/getting-started/wpf>.

Keep the decision trail in `orchestrate/bentago/decisions.tsv`. Keep the verification ledger in `orchestrate/bentago/ledger.tsv`. Keep generated evidence in `artifacts/verification/` and operator review media in `artifacts/review/`.
