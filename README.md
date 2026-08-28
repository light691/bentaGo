# BentaGo

Offline-first peddling POS for grocery route selling.

## What this folder is

This is the agent handoff for BentaGo: a Windows laptop POS used by one or more peddlers who take allocated grocery stock from a main store, sell it on route, collect payment, return unsold stock, and reconcile cash and inventory.

The existing grocery POS remains the system of record for the main store. MVP integration is deliberately CSV-based so the project can be deployed without depending on undocumented access to the existing POS database.

## Read and build in this order

1. `docs/00-pstack-style-master-plan.md` — pre-delegation plan, decision gates, milestones, and completion proof.
2. `docs/01-product-requirements.md` — product intent, scope, workflows, business rules, and acceptance criteria.
3. `docs/02-technical-architecture-and-implementation-plan.md` — architecture, schema, contracts, implementation phases, test plan, and agent task order.
4. `docs/03-ui-ux-keyboard-shortcuts-philippines.md` — screen designs, workflows, shortcut map, receipt conventions, and local operating assumptions.
5. `docs/04-agent-delegation-contracts.md` — bounded work packages, ownership, dependencies, and integration rules.
6. `docs/05-pstack-program-execution-plan.md` — the checked nine-PR program, live verification lanes, review gates, and delegation order.

Agents should not begin UI polish, hardware integration, or import/export work before the domain rules and transaction boundaries in document 02 are agreed.

## Locked MVP stack

- C# / .NET 10
- WPF desktop application with MVVM
- SQLite with Entity Framework Core
- USB barcode scanner in keyboard-HID mode
- ESC/POS thermal receipt printer through an abstract printer service
- CSV import/export for the existing POS
- Self-contained Windows x64 publishing
- Velopack for installation and updates

## MVP boundary

Included: local login and roles, peddler management, product catalog import, stock loading, trip sessions, barcode/manual sales, Cash/GCash/Maya/bank transfer/credit/split payments, receipts, voids and returns, damage/loss, daily cash close, optional physical stock count, audit log, reports, CSV exchange, backups, and printer/scanner diagnostics.

Deferred: real-time cloud sync, GPS and route optimization, commissions/payroll, loyalty, online ordering, e-invoicing, accounting integrations, multi-branch administration, and payment-provider APIs.

## Recommended phase order

1. Confirm operating assumptions and CSV samples with the client.
2. Create the solution, configuration, logging, database, migrations, and backup foundation.
3. Implement domain entities, inventory ledger, trip state machine, pricing, payments, and reconciliation services.
4. Add product and stock-load import/export with validation and import logs.
5. Build admin setup screens and peddler inventory screens.
6. Build the keyboard-first POS and transactional checkout.
7. Add receipt printing, voids, returns, adjustments, and diagnostics.
8. Build close/reconciliation and reports.
9. Test failure recovery, real hardware, representative Philippine data, and installer upgrades.
10. Pilot with one peddler, then expand only after reconciliation results are trusted.

## Important product principle

Track inventory movements, not only ending snapshots. Every load, sale, return, damage, loss, and correction must have a reason, reference, actor, timestamp, and ledger entry.
