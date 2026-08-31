# BentaGo

BentaGo is the main point of sale for one Philippine sari-sari store. It is a Windows desktop application that runs fully offline on a single machine at the store counter, with data in a local SQLite database. One operator works the till at a time.

The project began as a route-selling system and pivoted on 2026-08-30 to this store POS. The old route-selling domain is gone. Historical mentions of that program refer only to what changed.

## What BentaGo is

One Windows machine sits at the store counter. One store, one till, one warehouse. No cloud service exists. A sale never needs a network.

The day is the accounting unit. The register opens in the morning, sells all day, and closes the drawer at night. The day close compares expected cash to counted cash and labels the result `BALANCED`, `SHORTAGE`, or `OVERAGE`. A closed day is locked. Reopening it requires a reason and an approver.

Stock enters through purchases and receiving documents. A receiving record has a supplier, line items, unit cost, date, reference, and notes. It starts as a draft and becomes submitted. Only a submitted receiving changes on-hand stock.

Customers can buy on credit, recorded as utang. The system keeps named customers, outstanding balances, pay-downs, and a ledger of every credit and payment. A credit sale is a receivable and is excluded from expected cash.

Corrections are `VOID`, `RETURN`, `DAMAGE-LOSS`, and `ADJUSTMENT`. Each carries a reason and, where the grant requires it, an approver. Each creates a reversing movement. Original records stay visible.

The receipt is the source of truth for a sale. Receipts print on an 80mm thermal printer in ESC/POS at 42 columns, with a header from the store profile: name, address, contact, and TIN.

Three roles exist: Cashier, Supervisor, and Admin. Every sensitive action records the actor, the terminal, the time, and the reason.

Payment methods are Cash, GCash, Maya, Bank Transfer, Credit and Utang, and Split. GCash, Maya, and Bank Transfer are recorded as payment methods. The app never connects to a payment provider.

## Program status

The work is tracked in units. `Done` means the store-POS MVP passes the pilot and every PR is merged.

- BG-001 to BG-008 merged. This is the old program, built before the pivot. PRs #18 to #28. Full test suite 457/457 green at that point.
- BG-P0 merged. Rewrote `docs/01-product-requirements.md` and `docs/05-pstack-program-execution-plan.md` to the store-POS domain.
- BG-P1 restarting. Domain pivot: deletes the old route-selling domain, introduces the day lifecycle, and resets the schema. An earlier Core-stage checkpoint was lost (the branch was never pushed and the old checkout was deleted), so the unit restarts from the Core stage. Application, Infrastructure, and App stages follow.
- BG-P2. Receiving.
- BG-P3. Customer credit, or utang.
- BG-P4. Till identity. Single-till MVP. Multi-till is future only.
- BG-P5. Design overhaul, first-run wizard, and store profile. Design details are decided in BG-P5.
- BG-009. Packaging, recovery, and the pilot.

## Tech stack

Verified from `global.json`, `Directory.Build.props`, `Directory.Packages.props`, and the project files.

- .NET 10. `global.json` pins SDK 10.0.400 with `rollForward: latestPatch` and no prerelease.
- WPF desktop application with MVVM. `BentaGo.App` targets `net10.0-windows` with `UseWPF` true.
- SQLite with Entity Framework Core 10.0.4.
- Central package management in `Directory.Packages.props`. Other pinned packages: Serilog for logging, BCrypt.Net-Next for password hashing, xUnit for tests.
- `Nullable` enable, implicit usings, `LangVersion` latest, deterministic builds.
- Velopack packaging is planned in BG-009. It is not yet a package reference.

The pinned SDK is not installed on this machine, so the commands below describe what the repo pins, not what is installed here.

## Build and test

From the repository root:

```powershell
dotnet restore
dotnet build -c Release
dotnet test -c Release
dotnet format --verify-no-changes
```

Program gates require zero errors and zero warnings on build, a fully green test run, and zero format diagnostics.

The app is the WPF project:

```powershell
dotnet run --project src/BentaGo.App
```

This runs on Windows only.

## Repository layout

- `BentaGo.sln` at the root.
- `src/BentaGo.Core` holds the domain.
- `src/BentaGo.Application` holds use cases and services.
- `src/BentaGo.Infrastructure` holds EF Core and SQLite storage.
- `src/BentaGo.App` is the WPF application.
- `tests/BentaGo.Tests` holds the test suite.
- `docs/` holds the product and process documents.
- `orchestrate/bentago/HANDOFF.md` holds the program state and locked decisions.
- `artifacts/` holds verification evidence and review captures.

## Documentation and read order

Read the current documents first.

1. `docs/01-product-requirements.md` is the current PRD. It is the authority for product scope, business rules, and the non-goals list.
2. `docs/05-pstack-program-execution-plan.md` is the current execution plan. It defines units BG-P0 through BG-P5 and BG-009, with verification gates.
3. `docs/00-pstack-style-master-plan.md` is process law. It still applies. Its domain examples predate the pivot, so read its rules, not its examples.
4. `docs/02-technical-architecture-and-implementation-plan.md`, `docs/03-ui-ux-keyboard-shortcuts-philippines.md`, and `docs/04-agent-delegation-contracts.md` predate the pivot. Their domain sections are superseded and will be rewritten in BG-P1 and BG-P5. Their process parts, such as delegation contracts, still inform the program.
5. `docs/hardware-profile.md` records the selected scanner, printer, and drawer and the printer and scanner contracts. Its verification-gate references predate the pivot.

## MVP boundary

From `docs/01` section 6, the store POS MVP includes session and day lifecycle, catalog, receiving, sales, corrections, day close and reports, import and export, store profile and first-run wizard, and devices.

The MVP runs one store, one till, and one warehouse.

## Non-goals

Aligned exactly with `docs/01` section 9.

- No multi-till sync in the MVP. Multi-till is future only.
- No cloud. No network dependency.
- No loyalty program.
- No online payments. GCash, Maya, and Bank Transfer are recorded as payment methods. The point of sale does not connect to a provider and does not process a payment online.
- No promotion engine, no customer-specific pricing, and no commission.
- No route selling, no per-seller stock locations, and no outbound stock issuance to a seller.
- No GPS or route planning, no digital receipts, no Bluetooth or mobile version, and no direct integration with another point-of-sale system.

## Principles

These carry over from the original README and remain true.

- Track inventory movements, not only ending snapshots. Inventory never changes without a corresponding movement record. A receiving line, a sale, a return, damage or loss, and a manual adjustment each produce a movement with a reason, a reference, an actor, and a timestamp.
- No silent corrections. A difference at day close is labeled and recorded, never corrected quietly.
- The receipt is the source of truth. A sale is committed once. Retrying a print must not create another sale.
- Money is integer Philippine peso centavos. No floating point for money.
- Date boundaries use Asia/Manila. A day close rolls over at midnight in Asia/Manila.
- Offline first. Login, sale, print, day close, report, and backup never wait for a network.
- Backup and restore sit under Admin and are exercised again in BG-009, including recovery across an upgrade.
