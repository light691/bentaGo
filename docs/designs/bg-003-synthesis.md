# BG-003 Synthesis — Persist the Ledger and Atomic Operations

Synthesis of the BG-003 design pass (architect skill: ground → arena → agree → ship). The
arena record (§4) names the base candidate and every graft; the rest of the document is the
recommended shape for `docs/05` lines 183-231 ("Persist the ledger and atomic operations").

<!-- Provisional marker: this pass is design-only. Nothing in this document is committed
     as code; it is the contract the implementer builds against. -->

## 1. Explanation — why this shape

BG-002 left the movement stream in memory behind `ILedgerStore`. BG-003 makes that stream
the real SQLite stream: every business operation becomes one database transaction, balances
become a rebuildable projection of the movements, and the deterministic two-peddler day
survives restart and rebuild. The load-bearing idea of the synthesis is **reuse the fold,
don't re-implement it**: `LedgerState.Fold` already encodes every ledger invariant exactly
once (Validate per movement, VerifyCrossChecks across documents, structured DomainErrors
with the exact codes). The SQLite store therefore keeps a hot `LedgerState` as its guard
engine and replays persisted movements through the *same* `Fold` on rebuild — the persisted
world and the in-memory world are behaviorally identical by construction, and Lane 6
(rebuilt == cached) is a property of the design, not a test coincidence.

Why the shape is non-obvious: the fold's guards are compound and cross-referential
(STOCK_NEGATIVE must see the whole batch; RETURN_LIMIT needs cumulative returned-per-line;
REVOKED_ALREADY/REFUND_REVOKED need per-receipt and per-issue maps; VerifyCrossChecks needs
the document registry). Any design that re-encodes these in SQL creates a second source of
truth that drift can silently separate. Any design that drops the hot fold re-folds 100k
movements per operation and fails the perf contract (P95 checkout ≤ 500 ms, docs/05
lines 220-223). The arena produced three whole-shape answers to this tension; §4 records
which won and why, and what the losers contributed.

## 2. Provenance — locked contracts with citations

| # | Contract | Source |
|---|---|---|
| C1 | Append-only movement stream is the source of truth; `InventoryBalance` is a rebuildable cache | docs/02 §3 line 33; bg-002-synthesis "Invariant encoding map" |
| C2 | Money = integer centavos, never floats; UTC timestamps in storage, Asia/Manila display; integer quantities | docs/02 §4 line 40 |
| C3 | Required uniqueness: load number, receipt number, internal UUID (branded Guid ids); EF + SQLite are the stack | docs/02 §4 line 42; docs/02 §1 line 5 |
| C4 | No hard-delete of movements (append-only; reversal = new rows) | docs/02 §4 line 44; bg-002-synthesis |
| C5 | Checkout: validate trip status, product status, stock, quantities, payment totals, **idempotency key**; ONE SQLite transaction inserting sale, lines, payment lines, movements, balance cache, audit; print only after commit; failed print changes print status, not the sale | docs/02 §5 lines 48-50 |
| C6 | Stock load: one transaction — load, lines, transfer movements, both balance caches, audit | docs/02 §5 line 54 |
| C7 | Return/void: one transaction — reversing document, movement(s), balance changes, audit | docs/02 §5 line 58 |
| C8 | Close: compute totals from committed records, persist count+notes, calculate variance, transition trip; reopen requires admin reason + audit | docs/02 §5 line 62 |
| C9 | Back up **before** migrations; restore copies the current DB aside, validates, explicit confirmation | docs/02 §9 line 102 |
| C10 | BG-003 Files / Build / You-see / Verify unit / live / perf / merge — 8 unit buckets, 10 lanes, perf rules | docs/05 lines 183-231 |
| C11 | Lane-shaped brief: one DB transaction per operation; balances = rebuildable projection of movements; sequence assignment single source (batch first = last+1) INSIDE the transaction; documents INSERT in the SAME transaction as movements; no catalog tables (products are BG-005) | orchestrate/bentago/queue/bg-003-brief.md lines 6, 9-13, 16-21 |
| C12 | Fold semantics the store must reproduce: Validate / ApplyCommitted / Fold / VerifyCrossChecks with the exact DomainError codes | src/BentaGo.Core/Domain/Ledger/LedgerState.cs |
| C13 | Bind-then-append contract: register documents → `Bind` → `Append`; a document registered after the last `Bind` is not visible to the next append | src/BentaGo.Application/Contracts/ILedgerStore.cs lines 15-21 |
| C14 | Two-peddler scenario output byte-identical across runs and across store implementations (golden exists) | bg-002-synthesis; artifacts/verification/bg-002/ae40e6a/scenario-two-peddlers.golden.txt |
| C15 | Perf: P95 checkout ≤ 500 ms with 10k products / 100k movements; ledger rebuild ≤ 2 s; baselines recorded FIRST | docs/05 lines 218-223; docs/01 line 137 (SQLite transaction-safe under power loss) |

## 3. Invariant map (exact DomainError codes)

| Invariant | Encoded in | Enforcement / code |
|---|---|---|
| Sequences strictly increasing, batch-first = last+1, assigned inside the Append txn | Store transaction | `SEQ_ORDER` (fold); UNIQUE index on `Movements.Sequence`; watermark row |
| Movement rows never updated or deleted | Mapping (init-only) + SQLite trigger | `RAISE(ABORT)` trigger on UPDATE/DELETE of Movements |
| Timestamps UTC | `Moves` factories + storage | `UTC_REQUIRED` (fold); TEXT ISO-8601 with `Z` (offset never stored, so TEXT ordering is safe) |
| Money integer centavos | `Money` type + INTEGER columns | no float anywhere (docs/02 C2) |
| Document-kind legality per movement | sealed `DocumentRef` kinds + fold | `DOC_KIND` |
| Stock never negative, including within a batch and across two concurrent appends | Fold `CheckCell` inside the Append txn | `STOCK_NEGATIVE` (Append), `STOCK_INSUFFICIENT` (builder pre-check, informational) |
| Trip lifecycle | Fold | `TRIP_NOT_OPEN`, `TRIP_CLOSED`, `TRIP_NOT_CLOSED` |
| Reversals exist / match / not double | Fold | `REVERSES_UNKNOWN`, `REVERSES_MISMATCH`, `REVOKED_ALREADY`, `REVOKE_REFUNDED`, `REFUND_REVOKED`, `SALE_VOIDED` |
| Return / refund limits | Fold | `RETURN_LIMIT`, `REFUND_LIMIT` |
| Funds never negative; no fund for Credit | Fold | `FUND_NEGATIVE`, `NO_FUND` |
| Receipts/issues 1:1 with payment lines / sale lines | Fold `VerifyCrossChecks` on replay AND after every append | `INTEGRITY` (store maps `LedgerIntegrityException`) |
| Load number unique | Builder + DB UNIQUE | `DUP_LOAD`, `LOAD_NUMBER_REQUIRED` |
| Receipt number unique | DB UNIQUE | constraint → structured error at store |
| Idempotency key unique; second submit returns the SAME receipt reference, one sale | DB UNIQUE + store lookup inside the txn | duplicate → lookup existing sale by key → return its receipt reference |
| Nothing hard-deleted | No delete APIs, FK cascade off | docs/02 C4 |

## 4. Arena record

**Rubric** (applied criterion-by-criterion to every candidate; the cross-judge scored each):

- R1 Fold fidelity — reuses `LedgerState` for guard and replay (no parallel SQL re-encoding of fold rules); exact DomainError codes.
- R2 Concurrency — complete, correct lock discipline with the two-competitors interleaving (§6); committed stock never < 0.
- R3 Mapping tradeoffs — honest table across all three shapes on the four axes (replay fidelity, VerifyCrossChecks, migration churn, 100k-row rebuild perf); defensible verdict.
- R4 Seam — bind-then-append preserved; use-case services wrap builder+txn as deep modules; idempotency key → same receipt reference; print state outside the sale txn; InMemoryLedgerStore stays the test double; no Application→Infrastructure dependency.
- R5 Test plan 1:1 to docs/05 + lane mechanics with exact console streams.
- R6 Safety — golden scenario preserved; no invented DomainError codes; no catalog tables; backup-before-migration; -wal/-shm behavior intact.

[ARENA — fill after candidates A/B/C land: per-candidate scores, cross-judge verdict, base pick, grafts with source, rejections, dropouts.]

Provisional (lead): the front-runner is the normalized-row + hot-fold + durable-projection shape (A); the JSON candidate's fold-on-read cost analysis and the TPT candidate's DB-constraint inventory are expected grafts.

## 5. Decisions (synthesis-level, except where the arena overrides)

| # | Decision | Verdict |
|---|---|---|
| D1 | **Idempotency key**: client-supplied branded `CheckoutKey(Guid)` (Core), carried on `NewSaleCommand` and the `Sale` document; UNIQUE column; duplicate submit inside the txn → catch constraint → lookup by key → return the existing sale's receipt reference. Receipt NUMBER is a separate unique business ref (caller-supplied for BG-003; generation policy is a UI-era concern). | C5 + lane 4 |
| D2 | **Lock/transaction ordering** (§6): `BEGIN IMMEDIATE` → read watermark inside txn → refresh hot fold if stale → clone-apply guards → assign sequences → INSERT movements + bound documents + audit, update projections, bump watermark → COMMIT. busy_timeout ≥ 5000 ms, WAL, one connection per store, Pooling=False. | C11, C15, lane 5 |
| D3 | **Caches live in SQLite** as projection tables (cell balances, fund balances, observed, trip states, per-cell breakdown), updated in the same Append txn (docs/02 C5: "update balance cache" inside the checkout transaction). The HOT `LedgerState` behind the store is the guard/report engine; movements are the source of truth; rebuild recomputes projections from movements and verifies equality (Lane 6). | C1, C5 |
| D4 | **InMemoryLedgerStore stays** as the fast in-memory implementation of the (widened) ILedgerStore: Domain tests and the in-memory `scenario two-peddlers` keep using it; SqliteLedgerStore is wired where persistence matters (host DI, Persistence tests, live lanes, `scenario two-peddlers --persist`). Both implement the same bind-then-append contract; open-attempt set is cleared at each append result (verified safe: no Domain test depends on retaining a failed attempt's documents). | C13, C14 |
| D5 | **Bound documents insert in the Append txn** (trips, sales+lines+payments, loads+lines, returns+lines, voids, adjustments, counts, closes). TransferRef/ReopenRef are document-less (id carried on the movement row only). **No DB-level FK from movement rows to document tables**: document-less refs are legal (validated against the DomainTests forge/overdraw patterns); the fold's `DOC_KIND` + `VerifyCrossChecks` are the authoritative document-link checks. | C11, C12 |
| D6 | **Rebuild surface**: [ARENA — on ILedgerStore vs separate interface] | lane 6 |

## 6. Lock discipline and the canonical interleaving (two competing checkouts of the last unit)

State: `stock(sardines@route) = 1`; two store instances S1, S2 — separate connections,
separate hot folds at `LastSequence = N`.

1. S1: `BEGIN IMMEDIATE` acquires the write lock. S2's `BEGIN IMMEDIATE` blocks (busy
   handler, `busy_timeout`).
2. S1 in-txn: watermark read = N (matches hot) → no refresh; clone-apply guard:
   SaleIssue(1) → stock 0; INSERT sale + movements + projections; watermark → N+m;
   COMMIT. S1's hot state now stock 0.
3. S2 unblocks, acquires the write lock, and its in-txn reads see S1's committed rows.
4. S2 in-txn: watermark read = N+m ≠ hot.LastSequence = N → **refresh the hot fold from
   Movements inside the txn** → stock 0. (S2's builder pre-check had passed against its
   stale context — that check is informational, not authoritative.)
5. S2 guard: ApplyCommitted(SaleIssue(1)) → CheckCell(0−1) → **`STOCK_NEGATIVE`** →
   Append fails, txn rolls back, nothing inserted.

Why this is airtight: SQLite serializes writers (one write lock at a time under WAL), and
every Append runs its guards against a fold state that includes every movement committed
before the write lock was acquired — the in-txn watermark refresh closes the stale-race, so
two appends can never both validate against the same pre-commit state. Same-key duplicates:
the second INSERT violates the UNIQUE CheckoutKey index inside its txn → rollback → lookup
by key → return the first sale's receipt reference (D1, lane 4). Committed stock never goes
below zero (lane 5); the in-txn refresh keeps the fast path at one cheap `SELECT` for the
single-writer case (C15).

## 7. Movement payload inventory (anchor of the mapping tradeoff, §8)

Every mapping candidate captures the same information per movement. One row per movement,
kind discriminator + normalized payload columns, needs these nullable columns:

| Column | Kinds using it |
|---|---|
| `sequence` INTEGER PK unique | all |
| `kind` INTEGER (enum) | all |
| `when_utc` TEXT (ISO-8601 Z) | all |
| `actor_id` TEXT (Guid) | all |
| `doc_kind` INTEGER + `doc_id` TEXT | all (TransferRef/ReopenRef pairs are document-less) |
| `product_id` / `location_id` TEXT | 11 kinds (all stock/count movers) |
| `quantity_units` INTEGER | the 10 stock movers (NOT CountObserved) |
| `sale_line_id` TEXT | SaleIssue, ReturnToStock |
| `payment_line_id` TEXT | Receipt, ReceiptRefund, ReceiptRevoke |
| `method` INTEGER | Receipt, ReceiptRefund, ReceiptRevoke |
| `amount_centavos` INTEGER | Receipt, ReceiptRefund, ReceiptRevoke |
| `reverses` INTEGER (sequence) | SaleIssueRevoke, ReturnToStock, ReceiptRefund, ReceiptRevoke |
| `reason` TEXT | Damage, Loss, AdjustmentIncrease, AdjustmentDecrease, TripReopen |
| `trip_id` TEXT | TripClose, TripReopen |
| `observed_quantity` INTEGER | CountObserved |

13 nullable payload columns total; the widest row uses 7. Rebuild reads the table in
`Sequence` order into sealed movement records and hands them to `LedgerState.Fold`.

## 8. Arena tradeoff table — the three mapping candidates

[ARENA — fill from candidates' own tables; expected axes: replay fidelity, VerifyCrossChecks
impact, migration churn, 100k-row rebuild perf.]

## 9. Shape — how-to (recommended architecture)

[ARENA — fills the store strategy (hot fold + projections), the widened ILedgerStore member
list, the use-case seam with signatures, and the rebuild surface. Provisional outline:

- `ILedgerStore` gains `RegisterTrip/RegisterSale/RegisterReturn/RegisterLoad/RegisterVoid`
  + `Bind` on the C13 contract; both stores implement identically; scenario drops its cast.
- `SqliteLedgerStore : ILedgerStore` — one connection; hot `LedgerState`; watermark refresh
  (§6); Append = one txn; Rebuild; audit persistence.
- Use cases in Application implement the docs/02 service list (CreateSale=ICheckoutService
  with CheckoutKey, ImportStockLoad, VoidSale, CreateReturn, RecordDamage/RecordLoss/
  AdjustInventory, SubmitPhysicalCount, SubmitCashClose, ReopenTrip/CloseTrip); each wraps
  builder → register → bind → append(+audit) in one call. Print state lives in a
  PrintJobs/print-status record OUTSIDE the sale txn (C5).]

## 10. File plan

- `src/BentaGo.Core/` — add the branded `CheckoutKey` type [D1]. No fold changes expected.
- `src/BentaGo.Application/Contracts/` — ILedgerStore widened [ARENA]; use-case interfaces.
- `src/BentaGo.Application/Services/` — use-case implementations; InMemoryLedgerStore aligned
  to the widened contract; TwoPeddlersScenario cast removed.
- `src/BentaGo.Infrastructure/Persistence/` — EF mappings + migrations (Movements, documents,
  projections, LedgerMeta), SqliteLedgerStore, Rebuild.
- `src/BentaGo.Infrastructure/Backup/` — pre-migration backup hook (reuse BackupService; docs/02 line 102).
- `tools/BentaGo.Tools/Program.cs` — lane commands + `scenario two-peddlers --persist`.
- `tests/BentaGo.Tests/Persistence/` — the eight buckets.
- `orchestrate/bentago/scripts/bg003-live.ps1` — lane runner (text evidence named `bg-003-*.png`).

## 11. Implementation order (sequence of verifiable units)

p1 Seam first, in memory: widen ILedgerStore, align InMemoryLedgerStore, de-cast the
   scenario. Gate: Domain suite green, golden byte-identical.
p2 Mapping + migrations (non-destructive; unique indexes; append-only trigger).
p3 SqliteLedgerStore + host wiring.
p4 Use cases + print-status seam + tool commands + migration-backup hook.
p5 Persistence tests (8 buckets) + bg003-live.ps1 + perf baselines FIRST, then probes.
p6 Audit (ledger/transaction/migration/recovery verdicts at head SHA) → rebase onto main →
   prove patch unchanged (diff SHA) → operator squash-merge.

## 12. Test plan (1:1 map to docs/05)

| Bucket | Requirement source | What the test proves |
|---|---|---|
| U1 Rollback | docs/05 unit line 203; lane 3 | inject failure before commit → zero partial sale rows |
| U2 Concurrent checkout | lane 5 | two connections/sales; one wins; committed stock ≥ 0 |
| U3 Unique keys | lanes 4, 1 | CheckoutKey / ReceiptNumber / LoadNumber unique |
| U4 Ledger rebuild | lane 6 | rebuilt == cached exactly; Fold used for rebuild |
| U5 Close lock | lane 9 (bg-002) | mutation after close rejected until reopen |
| U6 Migration | lane 9 | prior-schema fixture migrates; ledger totals preserved; backup created first |
| U7 Backup | lanes 9-10 | -wal/-shm consistent; quick_check + SHA-256 |
| U8 Restore | lane 10 | restored reports match pre-upgrade fixture |
| + Idempotency | lane 4 | same key twice → one sale, identical receipt references |

## 13. Live-lane mechanics

Each lane runs `BentaGo.Tools` under a fresh temp data root (`--data-root <dir>` or
`BENTAGO_DATA_ROOT`); the runner captures stdout/stderr and writes the evidence file
`artifacts/verification/bg-003/<head>/bg-003-<lane>.png` — a UTF-8 text snapshot of the
console (bg-002 precedent: text evidence files; the `.png` name is the docs/05 contract;
no image-capturing model is involved).

| Lane | Tool command (draft) | stdout contract | PASS condition |
|---|---|---|---|
| L1 stock load | `ledger load ...` | per-movement lines (scenario renderer) + both-location balances + `LANE: stock-load` | every movement reconciles; both locations match |
| L2 checkout | `ledger checkout ...` | sale/line/payment summary, movements, balances, audit line, `LANE: checkout` | all five artifacts commit together |
| L3 rollback | `ledger checkout --fail-before-commit ...` | failure + `LANE: rollback`; probe dump | zero partial sale rows |
| L4 idempotency | `ledger checkout --key K ...` ×2 | second run prints the same `receipt-reference` + `duplicate:` line | one sale; both receipt refs equal |
| L5 concurrency | two concurrent `ledger checkout` processes | each prints ok/`STOCK_NEGATIVE` + final stock dump | stock never < 0; at most one sale of the last unit |
| L6 rebuild | `ledger rebuild ...` | `rebuilt-balances:` + `cache-balances:` + equality line | rebuilt == cached exactly |
| L7 reversals | `ledger void ...` + `ledger return ...` | original + reversing rows + reconcile block | originals remain; reversals reconcile |
| L8 power loss | `ledger checkout --crash-after-write`, then restart | crash line `written-rows=<n>`; restart dump | full txn or none |
| L9 migration | prior-schema fixture → `foundation migrate ...` | `BACKUP_CREATED:` path + `TOTALS:` preserved | backup first; totals preserved |
| L10 restore | `foundation restore <backup> ...` | `RESTORED:` + report block | matches pre-upgrade fixture |

## 14. Risks

- R1 **Restore-while-connected**: RestoreService swaps the DB file behind a path; the
  long-lived ledger connection must be disposed/reopened around restore (lane 10) — specify
  the close→swap→reopen ordering in the runner.
- R2 **Pre-migration backup deadlock**: DbInitializer must run the backup on a SEPARATE
  short-lived connection before `MigrateAsync`, not on the store's connection.
- R3 **Hot-fold staleness across processes**: two store instances share one file; the
  watermark check inside the txn (not before it) is what makes lane 5 correct — an
  implementer who checks staleness before `BEGIN IMMEDIATE` reintroduces the race.
- R4 **100k-row rebuild ≤ 2 s**: EF materialization must be column-pruned and sequential;
  validate early with the perf probe (docs/05 lines 218-223) before filling the p2 mapping.
- R5 **DateTimeOffset TEXT ordering**: only UTC (`Z`) ever stored; never store offsets —
  ISO-8601 Z text sorts correctly for replay joins.
- R6 **InMemoryLedgerStore open-attempt change**: clearing the open set on append result is
  behavior change; the Domain suite (43 tests) is the regression gate at p1.
- R7 **Migration backup on a fresh DB**: `foundation migrate` on a brand-new root must not
  require a backup of a nonexistent DB (backup-first only when the DB file exists and
  pending migrations exist).

## 15. Open questions the implementer must decide first

1. Does the widening of `ILedgerStore` (Register*/Bind) live on the interface itself, or on
   a second interface (`ILedgerDocumentWriter`) that `TwoPeddlersScenario` types against, to
   keep `ILedgerStore` a pure mutation seam? (Interface-depth call.)
2. `Rebuild()` on `ILedgerStore` vs on a separate `IStoreMaintenance` — both stores must
   support it either way; the choice is about surface.
3. Receipt-number generation policy (per-trip per-day sequence? global?) is out of BG-003
   scope — caller-supplied with UNIQUE enforcement, but the implementer should confirm the
   usage services do not need to generate numbers to satisfy lane 4's "same receipt
   reference" wording.
4. busy_timeout value: 5000 ms default is recommended; confirm acceptable worst-case lane-5
   wait before failing the second writer.
5. Whether `CheckoutKey` should ride on the `Sale` document (fold-visible) or only on the
   command + DB column (smaller core change, divergence between stores on in-memory
   enforcement of idempotency) — the synthesis leans toward on-document.

## 16. Next implementation step

p1 of §11: widen `ILedgerStore` with the Register*/Bind contract and align
`InMemoryLedgerStore`, then re-run the Domain suite and the golden scenario — the seam must
prove itself in memory before any persistence code exists.