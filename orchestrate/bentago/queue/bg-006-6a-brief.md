# BG-006a — sales domain core (CheckoutSession + search + history; no App)

Status: READY TO DISPATCH. THE CONTRACT IS `orchestrate/bentago/queue/bg-006-arch.md` (510 lines) — read it in FULL first; it is normative (decisions D1–D10, module map, signatures, tests plan). This brief adds standing rules + the expected-delta list; the package supersedes it on any conflict.

## Scope (per package's "Next implementation step" + module map)
- Core (additive only): `DiscountType`/`DiscountRates.Statutory(20,20)`/`DiscountPolicy.Allowed` (pure floor-truncation math), `PrintStatus`, `NewSaleLine.DiscountType = None` optional end param, builder rules `DISCOUNT_TYPE_MISMATCH` + `BANK_REFERENCE_REQUIRED`.
- Application (new): `CheckoutSession` (+State enum), `CheckoutSessionFactory`, `ActorGuard`, `CommittedSaleNumberSource`/`FixedNumberSource`, `SaleQueryService`, `ProductSearchService` + the four contracts (ICheckoutSession/Factory, ISaleNumberSource, ISaleQueryService, IProductSearchService) + records (SessionLine, SavedPaymentLine, SavedSaleState, SaleHistoryFilter/Row, SaleLineDetail, SaleDetail, ProductHit). `ICheckoutService`/`ILedgerStore`/`InMemoryLedgerStore`/`UseCaseServices.CheckoutService`/`TwoPeddlersScenario` MUST be byte-behavior-identical (the package pins this).
- Infrastructure: `ProductStore` + FindByBarcodeAsync/SearchByNameAsync (EF, escape wildcards, NOCASE index), migration `<ts>_BG006_01_AddSalesIndexes` (3 raw indexes ONLY: IX_Products_Name_NOCASE, IX_Sales_TripCreated, IX_PaymentLines_Reference; no tables/no data; Down drops each), `BentaGoHost` registrations (4 singletons).
- Tests under `tests/BentaGo.Tests/Sales/` (namespace BentaGo.Tests.Sales) per the package's test list: SalesTestBase + the 9 test files (ProductSearch, DiscountPolicy, CheckoutSession, ExactlyOnce, ReceiptNumberPolicy, PaymentAssembly, ActorBinding, Oversell, HistoryQuery, SalesIntegration + schema probe of the 3 indexes).

## Expected deltas (known-recurring class — DO NOT MISS)
1. **Migration-count assertions**: adding the 7th migration means MigrationTests/BootstrapTests (count 6→7) AND BackupTests (suffix assertion — the 5a/5c bug class; it now expects `BG006_01_AddSalesIndexes` as the DatabaseVersion suffix) must be updated. Check ALL `EndsWith("BG...")`/count pins; if BackupTests does not pin the suffix, verify and report.
2. **Scenario/golden byte-stability**: TwoPeddlersScenario untouched; PersistedGoldenTests (fresh-in-memory == SQLite == reopen == rebuild) byte-identical; LaneRunner.Lane2 pins sales==4/lines==4/payments==5/movements==28 unchanged. Your new migration must not change existing raw-table indexes (the fold reads are index-independent — verify the 3 new indexes exist WITHOUT altering existing ones).
3. **DI/host**: new singletons registered; existing registrations untouched (the 5d-core 314-315 suite must pass unchanged or with only the above assertion updates).

## Deliverables (2 commits, milestone-split, explicit paths, NO dotnet format, NO App/XAML/docs changes)
- Commit 1 (Core+Application): the pure/policy/contract/impl layer + session/search/query sources + Sales tests that need no Infrastructure changes? (Milestone judgment — CheckoutSessionTests run against real SQLite via SalesTestBase, so they may need commit 2's migration for the schema probe only; keep migration+ProductStore+host in commit 2.)
- Commit 2 (Infrastructure+tests): ProductStore queries, BG006_01 migration, BentaGoHost registrations, integration/history/schema-probe tests, migration-count assertion updates.
- Build pinned SDK (`C:\Users\nicky\.dotnet`) = 0 warnings/0 errors. NO dotnet test (coordinator chain). NO dotnet format.

## Acceptance (coordinator chain after your push)
Build 0 warnings; test = 315 + new green; format 0 diagnostics; goldens byte-identical (PersistedGoldenTests + tools GOLDEN_IDENT/RESTART/REBUILD if you run them); no App/XAML/docs diff vs your base.

## Report
SHAs, per-commit files, every interpretation (esp. any deviation from the arch package — the package says deviations are signal: surface them with reasons), migration-count assertion changes, build result, push origin bg-006a. Never ask mid-flight; conservative documented fallback + report.
