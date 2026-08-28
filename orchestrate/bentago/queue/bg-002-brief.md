# Brief: BG-002 domain model (draft — dispatch after BG-001 merge)

**Contract source:** docs/05 lines 133-182 (Files/Build/You see/Verify unit/Verify live/Verify perf). Gate 0 rows 2,4,5,6,9,10 + docs/01/02 domain rules. Authored from docs/05; worker inlines nothing else.

## Scope (from docs/05 BG-002)
- `src/BentaGo.Core/Domain/`: Money (integer centavos), Quantity (integer, >0 invariant), IDs (typed Wrapper types: ProductId, PeddlerId, TripId, SaleId, LineId?), InventoryLocation, Movement (movement ledger events), Trip, Sale, Payment, Return, Adjustment, Count, Close, AuditEvent — all as types that REJECT illegal states (branded primitives, sealed records, no EF/WPF/CSV/printer/fs types).
- `src/BentaGo.Application/Contracts/`: public application contracts (IInventoryService? per docs/02): ISaleProcessor, IInventoryBook, IScenarioRunner — decide shapes in an architect pass (use architect skill before signing).
- `tests/BentaGo.Tests/Domain/`: scenario builders + invariant tests covering every state transition, payment-total invariant, return limit, reversal reference, stock ownership rule, centavo calc, Manila display conversion.
- `tools/BentaGo.Tools` new command `scenario two-peddlers`: prints complete ordered business event sequence + exact expected cash and stock balances WITHOUT SQLite.

## Lane-shaped acceptance (live lanes use the tool, isolated outputs)
L1 cash sale exact centavos; L2 GCash/Maya/bank (revenue rises, physical cash not); L3 credit (reference exists, cash remittance excludes); L4 split payment sum == total; L5 underpaid split refused before any event; L6 wrong peddler location refused; L7 void reversal points to original + preserves records; L8 double partial return capped at sold qty; L9 closed-trip mutation fails until authorized reopen; L10 two-peddler scenario byte-identical repeat.

## Perf
100k movements eval to balances; 5 runs after warmup; median <= 1s and allocations within 10% of recorded budget.

## Gate/spawn notes
- Not review-gated on interaction, but operator merged; reconcile with plan's "no review gate" — still needs clean architecture + invariant verdict by interrogate at head.
- Verify new head SHAs; use the content-exact pattern (proven): author exact files in queue/bg-002/<phase>-p<no>/ and dispatch copy/build/commit workers.
- Version: 1. Dispatch conditions: BG-001 merged into main; base = new main head.
