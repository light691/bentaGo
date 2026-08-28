# BG-003 — Persist the ledger and atomic operations

Lane-shaped brief (docs/05 lines 183-231). Depends on BG-002 MERGED. NOT review-gated (no interaction change) → full merge path after audit.

## Wait, what
The BG-002 in-memory movement stream becomes the real SQLite stream behind `ILedgerStore`. One DB transaction per business operation; balances are a rebuildable projection of the movements. Product catalog and products come in BG-005 — use a ProductsRepository seam with an in-memory double only if a foreign key demands it; otherwise no catalog tables.

## Files
- `src/BentaGo.Infrastructure/Persistence/` — EF Core mappings + migrations for: movement rows (kind + payload columns), trips, sales, sale lines, payment lines, sale voids, returns + return lines, stock loads + lines; append-only means NO UPDATE on movement rows ever.
- `src/BentaGo.Infrastructure/Persistence/SqliteLedgerStore.cs` — implements `ILedgerStore` over one connection; `Append` = one transaction: insert movement rows + advance cached-balance tables; sequence assignment MUST be the single source (use the same monotonic rule as InMemoryLedgerStore: batch first = last+1, in order); documents insert in the SAME transaction as their movements.
- `src/BentaGo.Application/Contracts/ISaleService.cs` etc. — application use cases: commit sale (idempotency key → receipt reference), load, void (reversal), return, adjustment, count, close (trip lock), reopen — all call builders then store.Append in one transaction, print after commit (print state OUTSIDE the sale transaction).
- `tests/BentaGo.Tests/Persistence/` — real SQLite files: rollback, concurrent checkouts (two connections), unique keys, ledger rebuild, close lock, migration, backup, restore.
- Backup integration: the existing BackupService/RestoreService must treat the -wal/-shm companions correctly (already audited in BG-001) and be exercised by migration/restore lanes.

## Invariants (from BG-002, carried over)
- Append-only: movement rows are never updated/deleted; reversal = new reversing rows (BG-002 semantics).
- One transaction per operation; sale commits before print.
- Unique business refs: load number, receipt number; idempotency key UNIQUE — second submit returns SAME receipt reference; one sale.
- Concurrency: SQLite busy handling (busy_timeout + WAL); committed stock never below zero (the fold STOCK_NEGATIVE guard inside the same transaction).
- Rebuild = Fold over persisted movements; cached balances must equal rebuilt exactly (source of truth = movements).
- Restart/power-loss: a transaction is either fully committed or gone; SQLite recovery tested by the lane.

## Evidence
- Unit: `dotnet test -c Release --filter Persistence` green.
- Live lanes L1-L10 EXACTLY per docs/05 (files named `bg-003-*.png` under artifacts/verification/bg-003/<head>/): stock load reconcile; checkout atomic; rollback leaves zero partial state; idempotency (one sale, same receipt ref); 2 competing sales → stock never below zero; rebuild == cached; void/return rows remain + reversals reconcile; forced kill → either full or none; prior-schema fixture migrates with backup first + totals preserved; restored backup matches pre-upgrade reports.
- Perf (pinned host): baseline FIRST — (a) trunk empty-transaction median, (b) BG-002 in-memory scenario time; then P95 checkout with 10k products/100k movements; interleave 100 checkouts trunk vs BG-003 head; FAIL if P95 > 500 ms or ledger rebuild > 2 s.

## Gate
- Audit (independent ledger/transaction/migration/recovery verdicts at head SHA) → rebase onto main, prove patch unchanged (diff SHA), operator squash-merge (delegated: coordinator merges, records verdicts first).
