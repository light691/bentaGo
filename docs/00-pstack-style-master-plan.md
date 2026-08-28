# BentaGo Master Build Plan

**Purpose:** This is the pre-delegation operating plan. It applies pstack/Poteto principles—investigate first, make decisions explicit, bound each change, require proof, and independently review high-risk work—while staying within the BentaGo stack and MVP scope.

No agent may start application implementation until Gate 0 is signed off. No phase may be called complete without the proof listed for that phase.

## 1. Product outcome and proof of success

Build a Windows 16:9 laptop POS for Philippine grocery route sales. It must work without internet, treat every peddler as a separate inventory location, and preserve a defensible ledger for every inventory and money movement.

The final pilot proof is one complete two-peddler scenario:

1. Import products and load stock from CSV.
2. Load distinct quantities to two peddlers.
3. Complete cash, GCash/Maya, credit/utang, and split-payment sales offline.
4. Handle a successful reprint after simulated printer failure without a duplicate sale.
5. Record a return and a damaged item.
6. Close one trip balanced and one with an intentional cash and stock variance.
7. Export sales and reconciliation files that explain every variance.
8. Restore the database from a backup and confirm the same reports.

## 2. Immutable MVP boundaries

Locked: .NET 10, WPF, MVVM, SQLite, EF Core, keyboard-HID scanner, ESC/POS printer abstraction, CSV exchange, Windows x64 deployment, Velopack, local-first operation.

Excluded: cloud sync, web API, direct main-POS database integration, GPS/routing, commissions, loyalty, online ordering, payment-provider APIs, and multi-branch administration.

Rule: a useful V2 idea is recorded in a backlog note, never slipped into an MVP task.

## 3. Gate 0 — client decisions required before code

These choices affect schema, calculations, permissions, or compliance. Do not guess them.

| Decision | Options / question | Default if client has no preference | Owner |
|---|---|---|---|
| Sales tax/receipt policy | Is BentaGo a formal tax receipt, an internal sale slip, or both? What header/TIN data is required? | Internal sale slip; configurable header; no invented tax fields | Client |
| Product identity | Which field is authoritative: SKU, barcode, or both? Can a product have no barcode? | SKU + optional unique barcode | Client |
| CSV source | Provide actual product and stock-load exports, including encoding/delimiter and representative bad rows | Versioned UTF-8 CSV templates | Client |
| Price ownership | Can a peddler change price, or are imported prices locked? | Imported price locked; admin-only override with audit | Client |
| Discount policy | Who may discount, maximum amount/percent, approval requirement? | Disabled unless admin approves | Client |
| Utang / credit | Is credit included in MVP? Required customer identity, due date, and settlement process? | Record sale/reference only; no receivables ledger beyond reporting | Client |
| Cash expenses | May peddlers deduct fuel/meals/other expenses from remittance? | Not in MVP unless explicitly required | Client |
| Inventory count | Daily, weekly, on return, or exception-only physical count? | Cash daily; stock count weekly/on return | Client |
| Returns | Customer return window and disposition: peddler stock, main store, damaged? | Admin-approved with source reference | Client |
| Void authority | Peddler self-void versus supervisor/admin PIN; allowed timing? | Admin PIN and reason | Client |
| Hardware | Exact laptop resolution, scanner model/suffix behavior, printer model/interface/paper width | Test at 1366×768 and 1920×1080; 80 mm printer profile | Client |
| Users | Initial peddlers, admins, supervisors; one or multiple active trips per peddler? | One active trip per peddler | Client |

**Gate 0 exit:** written answers or accepted defaults, plus anonymized sample CSV files and actual hardware details.

## 4. Architecture review checkpoint

Before coding, run an independent design review against these non-negotiable assertions:

- Inventory movement is append-only source of truth; a balance is a derived cache.
- A checkout is one database transaction; receipt printing happens after it commits.
- Money uses integer centavos; time is stored UTC and displayed Asia/Manila.
- Every reversal references the original business document; no destructive edits.
- Closed trips cannot mutate without an authorized reopen audit event.
- Peddlers have separate inventory locations and cannot sell each other’s stock.
- CSV imports preview and validate before a full atomic commit.
- The WPF UI contains no business calculations beyond display formatting.

**Gate A exit:** schema diagram, state-transition table, transaction-boundary document, CSV contract, and reviewer confirmation that each assertion is testable.

## 5. Milestones

### M1 — Foundation and executable skeleton

Deliver solution structure, configuration, logging, DI, WPF shell at 1366×768, migrations, data folders, seeded admin, backup/restore safety, and automated test project.

Proof: clean machine/startup test creates and migrates a database; sample database can be backed up and restored; build and tests pass.

### M2 — Domain ledger and reconciliation engine

Deliver entity model, state machine, movement ledger, balance projection/rebuild, prices, payments, stock loads, sales, corrections, returns, voids, and close calculations as Application/Core services.

Proof: automated two-peddler scenario passes, with exact movement sequence and correct cash/stock variance calculations. This is the highest-risk milestone and gets independent code review.

### M3 — Data exchange and admin setup

Deliver product import, stock-load import preview/commit/error file, import history, exports, user/peddler management, trip creation, load approval, and inventory views.

Proof: real client CSV samples import deterministically; rejected rows are understandable; generated export matches seeded transactional data.

### M4 — Keyboard-first WPF selling flow

Deliver login, peddler home, sale screen, HID scan processing, product search, cart, permission-aware discount, payment entry, split payment, checkout result, and reprint state.

Proof: a keyboard-only operator completes representative sales at 1366×768; duplicate scanner Enter and repeat checkout cannot create duplicate sales.

### M5 — Hardware and correction workflows

Deliver printer/scanner diagnostics, ESC/POS adapter, receipt template, retry/reprint, void, return, damage/loss, adjustments, and audit views.

Proof: physically unplugged printer produces one saved sale plus a recoverable reprint; scanner test records raw input and product match.

### M6 — Close, reports, and pilot readiness

Deliver cash close, count entry, variance workflow, close/reopen permission controls, printable close report, required CSV reports, backup reminder, and installer/update package.

Proof: the end-to-end pilot scenario at the top of this document succeeds on real target hardware; a reviewer reconciles all values from reports without looking at the database.

## 6. Work sequencing and permitted parallelism

```text
Gate 0 → Gate A → M1 → M2 → M3 ─┬→ M4 → M5 ─┐
                                └→ report UI ─┤→ M6
                                              ↓
                                          Pilot review
```

M3 import services and a read-only report UI may run in parallel only after M2 publishes stable DTOs and test fixtures. M4 may start with mock services after M2 contracts are frozen, but it must not introduce domain rules. Hardware adapters may be stubbed during M4 and completed in M5.

Never run two agents against the same project or migration files simultaneously. One agent owns Core/Application schema changes at a time.

## 7. Evidence standard

Every work package must return:

- exact acceptance criteria met;
- focused tests executed and result;
- manual verification steps where UI/hardware is involved;
- migrations/configuration added;
- files changed;
- assumptions and known risks;
- a screenshot for each material WPF screen change;
- a short, reviewable diff.

“Build succeeds” alone is not evidence for accounting, inventory, import, printer, or reconciliation work.

## 8. Independent review triggers

Require a second agent/reviewer before merging changes that alter: ledger movement semantics; stock availability; money calculation; checkout transaction; idempotency; import commit logic; password/authentication; permissions; migration; backup/restore; printer reprint behavior; or trip close/reopen.

For UI-only work, review keyboard flow, 1366×768 layout, focus order, error copy, and match to the WPF screen reference.

## 9. Change control

New request → classify as bug, MVP requirement, refinement, or V2. For non-trivial changes, write a one-page decision record explaining the problem, options, choice, data impact, test impact, and rollback plan. No cross-cutting change proceeds directly from chat request to code.

## 10. Delegation readiness checklist

Delegation begins only when Gate 0 and Gate A are complete, `docs/04-agent-delegation-contracts.md` is followed, a single coordinator owns integration, and each agent receives only one bounded package with a branch/worktree and explicit proof criteria.

