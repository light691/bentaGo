# BG-P1b — Application layering (day domain)

Recreated 2026-08-30 during the docs alignment pass. The original brief was lost with the `C:\BentaGo` checkout; this reconstruction comes from `HANDOFF.md` section 9's resume notes (which were written by the session that ran P1a) plus `docs/05` unit BG-P1. Verified facts are marked; anything the reconstructor could not verify is marked ASSUMPTION.

Context for the runner: BG-P1 restarts from the Core stage (the prior P1a checkpoint `b310de3` is lost — branch never pushed, worktree deleted). This brief covers the Application stage that follows Core. Read `docs/05` unit BG-P1 and `HANDOFF.md` sections 2, 3b, 8 before starting. Branch `bgp1` from `origin/main`; one worktree; one writer.

## Goal

Retarget the Application layer to the day-keyed store-POS domain so that no live trip/peddler/`DailyClose`/`TripCash` concept survives in `src/BentaGo.Application`, and the screen-facing contracts speak in days, receiving, and utang vocabulary.

## Verified starting facts (from the P1a session's handoff notes)

- The day-keyed Core shapes are already designed and were implemented at the lost checkpoint; they must be re-implemented first (Core stage): Day document carries close facts; `DailyClose` is gone from Documents.cs; `ReconciliationReport` carries Day/Location; `Sale` carries Day. Movement kinds are `GoodsReceived` and `FundOut`; approved cash expenses are a `FundOut` into the expected-cash formula (coordinator-ratified).
- 16 Application files carried trip/peddler/`DailyClose`/`TripCash` references at the time of the lost checkpoint:
  - Contracts: `UseCases.cs`, `Reconciliation.cs`, `Sales.cs`, `ILedgerStore.cs`
  - Services: `UseCaseServices.cs`, `SaleQueryService.cs`, `ReconciliationReportService.cs`, `CheckoutSession`/`CheckoutSessionFactory`, `InMemoryLedgerStore.cs`, `ProductSearchService.cs`, `ReceiptCodec.cs`, `ReconciliationCsv.cs`, `UserAccountService.cs`
  - Scenarios to delete: `TripLifecycleScenario.cs`, `TwoPeddlersScenario.cs`
  - (The full 16-file list was in the lost brief; the above is the named subset. ASSUMPTION: remaining files are the view-model wiring that consumed those contracts — re-derive by grepping `Trip|Peddler|DailyClose|TripCash` in `src/BentaGo.Application` before editing.)

## Steps

1. Grep gate first: `Trip|Peddler|DailyClose|TripCash|Load` over `src/BentaGo.Application` and `tests/`. Record the hit list; it is the work inventory. Zero hits is the exit condition.
2. Retarget contracts first (`UseCases.cs`, `Reconciliation.cs`, `Sales.cs`, `ILedgerStore.cs`): day-keyed queries, day-scoped reconciliation input, sale/checkout contracts without trip context, ledger store widened per the Core shapes.
3. Then services in dependency order (`UseCaseServices.cs`, `SaleQueryService.cs`, `ReconciliationReportService.cs`, `CheckoutSession`/Factory, `InMemoryLedgerStore.cs`, `ProductSearchService.cs`, `ReceiptCodec.cs`, `ReconciliationCsv.cs`, `UserAccountService.cs`). One commit per coherent layer with a build gate: `dotnet build -c Release` 0 errors 0 warnings.
4. Delete `TripLifecycleScenario.cs` and `TwoPeddlersScenario.cs`; replace with a single-store day scenario (one till, one warehouse, day lifecycle) so tests and lanes have a seed. Keep the golden-scenario discipline: totals must be derivable by hand.
5. Gates per `docs/05`: build 0/0, full `dotnet test -c Release` green, `dotnet format --verify-no-changes` clean, grep gate from step 1 at zero live hits. Append a `ledger.tsv` row; PR to main per the standing delegation order.

## Non-goals / quarantine

- No Infrastructure changes here (schema reset and the real `SqliteAppDataSource` are P1c/P1d).
- No UI design work (BG-P5).
- No receiving/utang/till features (BG-P2/BG-P3/BG-P4). Contracts may reserve nothing for them beyond what `docs/05` already specifies.
