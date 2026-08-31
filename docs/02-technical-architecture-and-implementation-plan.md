# BentaGo Technical Architecture and Implementation Plan

**Pre-pivot document.** This plan was written for the former peddling product: trips, peddlers, stock loads, per-trip reconciliation. The product pivoted to a standalone store POS on 2026-08-30. The domain sections below are SUPERSEDED by `docs/01-product-requirements.md` and `docs/05-pstack-program-execution-plan.md` and are being retargeted by BG-P1. Read sections 3, 5, 6, and 10 as history of the old domain, not as the current plan.

Engineering rules that remain binding:

- Offline first. Local SQLite, no cloud, no web API, no direct database coupling (sections 1, 8, 9).
- WPF with MVVM. Thin UI, business rules in Application and Core, hardware and filesystem behind Infrastructure interfaces (section 2).
- Integer Philippine peso centavos and Asia/Manila date boundaries, never floating point for money (section 4).
- The inventory movement ledger is the source of truth and balances are a rebuildable cache (section 3).
- Commit the business transaction first and print only after; a failed print never changes the sale (sections 5, 8).
- CSV import and export discipline. Header-by-name mapping, preview, row errors, no partial commits, RFC-4180 exports (section 7).
- Build, test, and format gates, XAML resource rules, and the current phase plan (BG-P0 through BG-P5, then BG-009) live in docs/05.

## 1. Locked decisions

Build a Windows x64 desktop app in C#/.NET 10 using WPF and MVVM. Use SQLite with Entity Framework Core. Treat barcode scanners as keyboard-HID devices. Print through an ESC/POS adapter behind an interface. Exchange data with the existing POS through CSV. Package self-contained builds with Velopack. Store binaries under Program Files and writable database/backups under ProgramData.

Do not introduce cloud sync, a web API, or direct database coupling in MVP.

Note added after the 2026-08-30 pivot. CSV exchange here means the catalog import and the CSV exports that docs/01 defines. Direct integration with another point-of-sale system is a non-goal in docs/01. Everything else in this section stays binding.

## 2. Solution structure

```text
BentaGo.sln
├── BentaGo.App             WPF views, view models, navigation, commands
├── BentaGo.Core            entities, value objects, enums, invariants
├── BentaGo.Application      use cases, DTOs, validators, orchestration
├── BentaGo.Infrastructure   EF Core, SQLite, CSV, printing, backup, settings
├── BentaGo.Tests            unit, integration, persistence, workflow tests
└── BentaGo.Tools            optional CSV samples, printer diagnostics, migrations
```

Keep UI thin. Business rules belong in Application/Core services. Hardware and filesystem concerns belong behind Infrastructure interfaces.

## 3. Domain model

Pre-pivot. The entity list and the relationships below describe the peddling domain. docs/01 defines the current domain and BG-P1 resets the schema to it. The authoritative inventory of files to delete or convert is in `orchestrate/bentago/HANDOFF.md` section 8.

Three principles here survive the pivot and stay binding. The inventory movement is the source of truth. The balance is a rebuildable cache. Every movement references a business document and an actor. The peddler and trip entities do not carry over, and BG-P1 retargets the movement type names.

Core entities: `User`, `Employee`, `Peddler`, `Product`, `InventoryLocation`, `InventoryBalance`, `InventoryMovement`, `PeddlingTrip`, `StockLoad`, `StockLoadLine`, `Sale`, `SaleLine`, `PaymentLine`, `Return`, `ReturnLine`, `Adjustment`, `DailyClose`, `PhysicalCount`, `AuditEvent`, `ImportBatch`, and `AppSetting`.

Important relationships:

- User optionally links to an employee; an employee may have a peddler profile.
- Main Store and each peddler are inventory locations.
- A trip belongs to one peddler and one location.
- Sales belong to a trip and peddler; sale lines snapshot name, SKU, price, tax/discount inputs, and quantity.
- Inventory movement is the source of truth. `InventoryBalance` is a rebuildable cache.
- Every movement references a business document and actor.

Recommended movement types: `InitialLoad`, `AdditionalLoad`, `Sale`, `SaleVoid`, `ReturnToStock`, `ReturnToStore`, `Damage`, `Loss`, `AdjustmentIncrease`, `AdjustmentDecrease`, and `Transfer`.

## 4. Data design requirements

Pre-pivot in part. Integer centavos, UTC storage with Asia/Manila display, and the no-hard-delete rule stay binding. The uniqueness and index items that name load numbers, peddler locations, or trip status describe the old domain. docs/01 requires unique barcodes and unique, human-readable sale numbers and receiving references.

Use integer minor units for money (Philippine centavos) rather than floating point. Use UTC timestamps in storage and display local time (Asia/Manila) in the UI. Use decimal quantities only if the client sells by weight; otherwise use integer quantities.

Required uniqueness constraints: username, product SKU where present, barcode where present, load number, receipt number, and internal UUID. Add indexes for barcode, SKU, product name, peddler/location, trip status/date, sale date, and movement reference.

Do not hard-delete products, peddlers, trips, sales, payments, or movements. Use inactive/status fields.

## 5. Transaction boundaries

Pre-pivot in part. The checkout and return/void boundaries below stay binding in principle: validate first, commit the whole business transaction exactly once, and attempt printing only after commit. The Stock load boundary is superseded; stock enters through purchases and receiving per docs/01, delivered by BG-P2. The Close boundary describes the trip close. The current close is the day close in docs/01, with expected cash versus counted cash and a `BALANCED`, `SHORTAGE`, or `OVERAGE` label.

### Checkout

Validate trip status, product status, stock, quantities, payment totals, and idempotency key. In one SQLite transaction: insert sale, lines, payment lines, sale movements, update balance cache, insert audit event, and commit. Only after commit attempt printing. A failed print changes print status, not the sale.

### Stock load

Validate source stock and duplicate load number. In one transaction: create load and lines, create transfer movements from Main Store to peddler, update both balance caches, and audit approval.

### Return/void

Validate original reference and remaining returnable quantity. In one transaction: create reversing document, movement(s), balance changes, and audit event.

### Close

Compute totals from committed records, persist the submitted count and notes, calculate variance, and transition the trip to Closed. Reopen requires an admin reason and a new audit event.

## 6. Application services and contracts

Pre-pivot. The use case names below come from the peddling program. The trip use cases (`StartTrip`, `CancelTrip`, `ReopenTrip`, `CloseTrip`, `ImportStockLoad`, `GetPeddlerStock`) do not carry over. The rest map onto the store POS and are retargeted by BG-P1 through BG-P3: catalog import, sales and corrections, day close and reports, backup, and device tests. The rule about structured validation results stays binding.

Implement these use cases before building screens:

- `AuthenticateUser`
- `StartTrip`, `CancelTrip`, `ReopenTrip`, `CloseTrip`
- `ImportProducts`, `PreviewProductImport`, `ImportStockLoad`
- `SearchProducts`, `GetPeddlerStock`
- `CreateSale`, `RetryReceiptPrint`, `VoidSale`
- `CreateReturn`, `RecordDamage`, `RecordLoss`, `AdjustInventory`
- `CalculateCashClose`, `SubmitCashClose`, `SubmitPhysicalCount`
- `GenerateSalesReport`, `GenerateInventoryReport`, `ExportCsv`
- `CreateBackup`, `RestoreBackup`, `RunPrinterTest`, `RunScannerTest`

Return structured validation results with field/row-level errors. Avoid throwing expected user errors as generic exceptions.

## 7. CSV contracts

Pre-pivot in part. The `products.csv` contract matches the docs/01 catalog fields and stays. The `stock_load.csv` contract is superseded; the stock-load CSV exchange was removed in the pivot. The export column sets below include peddler and load columns. docs/01 defines the current exports: transaction detail, reconciliation, inventory, and receivable CSV, each RFC-4180, invariant, with Asia/Manila dates. The import rules at the end of this section stay binding.

Publish sample files under a future `samples/` folder and version the format.

`products.csv`: `sku,barcode,name,category,unit,cost,selling_price,is_active`.

`stock_load.csv`: `load_number,peddler_code,load_date,sku,quantity,unit_cost,notes`.

Exports should include stable IDs and human-readable references: `date,receipt_no,peddler_code,sku,quantity,unit_price,discount,payment_method,status` for transaction detail, and `sku,starting_qty,loaded,sold,damaged,lost,returned,ending_qty,variance` for inventory reconciliation.

Import rules: UTF-8 first, accept common Excel CSV delimiters if detectable, trim whitespace, preserve leading zeros in SKU/barcode, reject duplicate keys, validate dates and money, preview before commit, and never partially commit a batch unless the user explicitly chooses a safe per-row mode.

## 8. Hardware boundaries

`IBarcodeScannerInput` should treat scanner input as keyboard text ending in Enter, while allowing manual search. Provide a scan test screen that displays raw value and matched product.

`IReceiptPrinter` should expose print, retry, test page, and connection/status operations. Store receipt content as a renderable model so a PDF/text fallback can be tested without a printer. Never make printing part of the database transaction.

## 9. Security, backup, and operations

Use a well-reviewed password-hashing library/algorithm available for .NET; never store plaintext passwords. Require admin confirmation for voids, large adjustments, reopening, restore, and deleting import files. Record audit events for login failures, overrides, imports, exports, prints, closes, restores, and configuration changes.

Back up before migrations, once daily after close, and on demand. Keep a configurable number of dated backups. A restore flow must copy the current database aside, validate the selected backup, and require explicit confirmation.

## 10. Phase plan and agent task order

Pre-pivot phase plan, kept as history. The current phase plan is BG-P0 through BG-P5, then BG-009, in docs/05. Where the mapping is clean: Phase 0 corresponds to the BG-P0 docs rewrite. Phase 1 corresponds to the existing foundation plus the BG-P1 schema reset. Phase 8 corresponds to BG-009, packaging, recovery, and the pilot. The middle phases do not map one to one. docs/05 sequences the current work as BG-P1 domain pivot, BG-P2 receiving, BG-P3 customer credit, BG-P4 till identity, and BG-P5 design overhaul, first-run wizard, and store profile.

### Phase 0 — discovery lock

Collect real product and stock-load CSV samples; confirm receipt size, printer model, barcode rules, tax/discount policy, payment methods, credit/utang policy, daily close expectations, and who approves variances. Record decisions in a short ADR before coding.

### Phase 1 — foundation

Create solution/projects, dependency versions, configuration, logging, error handling, DI, navigation shell, database path, migrations, seed admin, backup service, and test harness. Acceptance: app starts, creates DB, migrates safely, logs, and backs up/restores a sample DB.

### Phase 2 — domain and ledger

Implement entities, enums, money/quantity value objects, invariants, DbContext, mappings, indexes, repositories or query services, and movement-ledger rebuild. Add tests for load/sale/return/damage/void and negative-stock prevention. Acceptance: a full trip can be represented without UI.

### Phase 3 — catalog and imports

Implement product CRUD, import preview/validation/commit, import history, stock-load creation, and export services. Acceptance: representative files import with clear rejected rows and never corrupt existing data.

### Phase 4 — admin and peddler setup

Build login, users/roles, peddler management, main-store and peddler inventory views, trip creation/start, and load approval. Acceptance: two peddlers have isolated inventory and permissions.

### Phase 5 — selling

Build POS shell, scanner focus, search, cart, quantity, discount policy, payment entry, change, checkout command, idempotency, and transaction-safe persistence. Acceptance: offline sale updates all expected records once.

### Phase 6 — print and corrections

Implement ESC/POS adapter, printer setup/test, receipt retry/reprint, voids, returns, damage/loss, and admin approvals. Acceptance: disconnected printer cannot duplicate a committed sale.

### Phase 7 — close and reports

Implement daily close, payment totals, cash variance, physical count, inventory variance, close lock, reports, CSV exports, and printable summaries. Acceptance: owner can reconcile a seeded trip from exported data.

### Phase 8 — hardening and pilot

Test power interruption/restart, duplicate scans, large imports, corrupted/locked CSV, printer unplugging, backup restore, migrations, permissions, dates, currency formatting, and upgrade/uninstall. Pilot with one peddler and real hardware; fix reconciliation issues before adding features.

## 11. Definition of done for every agent task

The task hygiene list below remains a reasonable baseline, but the binding gates now live in docs/05: zero-warning Release build, green test run, clean format check, the XAML resource rules, the one-writer rule, and the operator review gate for interaction-changing PRs.

The task has a small focused change, tests for normal and failure cases, no business rule in a view, migration updates where needed, clear user-facing validation, audit behavior for sensitive actions, and a short handoff note describing files changed and remaining risks.

## 12. Test matrix

Pre-pivot in part. Seed scenarios that name multiple peddlers belong to the old domain. The rest still applies to the store POS: split payment, credit sale, void, partial return, shortage, overage, and interrupted print. Day lifecycle, receiving, and reopen-with-approval scenarios come from docs/01 and the BG-P1 through BG-P3 lanes in docs/05.

Cover domain unit tests, Application service tests, SQLite integration tests, import/export golden files, WPF view-model tests, printer/scanner adapter tests, and end-to-end pilot scripts. Seed scenarios must include multiple peddlers, split payment, credit sale, void after close attempt, partial return, shortage, overage, and interrupted print.

