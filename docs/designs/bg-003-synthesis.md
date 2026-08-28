# BG-003 Synthesis — Persist the Ledger and Atomic Operations

Synthesis of the BG-003 design pass (architect skill: ground → arena → agree → ship),
covering `docs/05` lines 183-231 ("Persist the ledger and atomic operations"). This pass is
**design-only**: nothing here is code; this document is the contract the implementer builds
against and the record the audit reads.

Final concept in one line: **one `Movements` table (kind discriminator + normalized payload
columns) behind a hot in-memory `LedgerState` fold engine, durable balance-projection tables
written in the same `Append` transaction, `ILedgerStore` widened with document registration
so both stores share the bind-then-append contract, and the persisted two-peddler day
rebuilding the same balances through the same fold.**

## 1. Explanation — why this shape

BG-002 left the movement stream in memory behind `ILedgerStore`; its `Append` is the single
mutation seam, sequences are assigned batch-first = last+1, guards run through
`LedgerState.ApplyCommitted` on a clone, and `VerifyCrossChecks` catches document drift after
every batch. BG-003 makes that stream the real SQLite stream: every business operation
becomes one database transaction, balances become a rebuildable projection of the movements,
and the deterministic two-peddler day survives restart and rebuild.

The load-bearing decision is **reuse the fold, don't re-implement it**: `LedgerState.Fold`
already encodes every ledger invariant exactly once (Validate per movement,
VerifyCrossChecks across documents, structured `DomainError`s with the exact codes — C12).
Any design that re-encodes the fold's compound guards in SQL (RETURN_LIMIT needs cumulative
returned-per-line; REFUND_LIMIT/REVOKED_ALREADY need per-receipt maps; STOCK_NEGATIVE needs
the whole batch) creates a second source of truth that drift can silently separate; any
design that drops a runtime fold re-folds 100k movements per operation and fails the perf
contract (P95 checkout ≤ 500 ms, rebuild ≤ 2 s — C15). The chosen store therefore keeps a
**hot `LedgerState`** as its guard/report engine and replays persisted movements through the
*same* `Fold` on rebuild — the persisted world and the in-memory world are behaviorally
identical by construction, and Lane 6 (rebuilt == cached) is a property of the design, not a
test coincidence.

The arena (§4) produced three whole-shape answers to this tension — one `Movements` table
with normalized payload columns + a hot fold (A), JSON payload per kind + fold-on-read (B),
and 16 normalized kind tables + in-txn transient fold + checked caches (C). A won; B and C
contributed the durability, transaction-mode, idempotency, and test-seam grafts recorded in
§4.

## 2. Provenance — locked contracts with citations

| # | Contract | Source |
|---|---|---|
| C1 | Append-only movement stream is the source of truth; `InventoryBalance` is a rebuildable cache | docs/02 §3 line 33; bg-002-synthesis "Invariant encoding map" |
| C2 | Money = integer centavos, never floats; UTC timestamps in storage, Asia/Manila display; integer quantities | docs/02 §4 line 40 |
| C3 | Required uniqueness: load number, receipt number, internal UUID (branded Guid ids); EF + SQLite are the stack | docs/02 §4 line 42; docs/02 §1 line 5 |
| C4 | No hard-delete of movements (append-only; reversal = new rows) | docs/02 §4 line 44; bg-002-synthesis |
| C5 | Checkout: validate trip status, product status, stock, quantities, payment totals, **idempotency key**; ONE SQLite transaction inserting sale, lines, payment lines, movements, **balance cache**, audit; print only after commit; failed print changes print status, not the sale | docs/02 §5 lines 48-50 |
| C6 | Stock load: one transaction — load, lines, transfer movements, both balance caches, audit | docs/02 §5 line 54 |
| C7 | Return/void: one transaction — reversing document, movement(s), balance changes, audit | docs/02 §5 line 58 |
| C8 | Close: compute totals from committed records, persist count+notes, calculate variance, transition trip; reopen requires admin reason + audit | docs/02 §5 line 62 |
| C9 | Back up **before** migrations; restore copies the current DB aside, validates, explicit confirmation | docs/02 §9 line 102 |
| C10 | BG-003 Files / Build / You-see / Verify unit / live / perf / merge — 8 unit buckets, 10 lanes, perf rules | docs/05 lines 183-231 |
| C11 | Lane-shaped brief: one DB transaction per operation; balances = rebuildable projection of movements; sequence assignment single source (batch first = last+1) INSIDE the transaction; documents INSERT in the SAME transaction as movements; no catalog tables (products are BG-005) | orchestrate/bentago/queue/bg-003-brief.md lines 6, 9-13, 16-21 |
| C12 | Fold semantics the store must reproduce: Validate / ApplyCommitted / Fold / VerifyCrossChecks with the exact DomainError codes | src/BentaGo.Core/Domain/Ledger/LedgerState.cs |
| C13 | Bind-then-append contract: register documents → `Bind` → `Append`; a document registered after the last `Bind` is not visible to the next append | src/BentaGo.Application/Contracts/ILedgerStore.cs lines 15-21 |
| C14 | Two-peddler scenario output byte-identical across runs and across store implementations (golden exists) | bg-002-synthesis; artifacts/verification/bg-002/audit-fix/golden.txt |
| C15 | Perf: P95 checkout ≤ 500 ms with 10k products / 100k movements; ledger rebuild ≤ 2 s; baselines recorded FIRST | docs/05 lines 218-223; docs/01 line 137 (SQLite transaction-safe under power loss) |

## 3. Invariant map (exact DomainError codes)

| Invariant | Encoded in | Enforcement / code |
|---|---|---|
| Sequences strictly increasing, batch-first = last+1, assigned inside the Append txn | Store transaction (sequence = hot fold + `LedgerHead`) | `SEQ_ORDER` (fold); UNIQUE/rowid PK on `Movements.Sequence` |
| Movement rows never updated or deleted | Mapping (init-only) + SQLite trigger | `RAISE(ABORT)` trigger on UPDATE/DELETE of Movements |
| Timestamps UTC | `Moves` factories + storage | `UTC_REQUIRED` (fold); TEXT ISO-8601 `'O'` with `Z` (offset never stored, so TEXT ordering is safe) |
| Money integer centavos | `Money` type + INTEGER columns | no float anywhere (C2) |
| Document-kind legality per movement | sealed `DocumentRef` kinds + fold | `DOC_KIND`; `DocumentKind`/`Kind` CHECK-restricted at the DB |
| Stock never negative, including within a batch and across two concurrent appends | Fold `CheckCell` inside the Append txn | `STOCK_NEGATIVE` (Append); `STOCK_INSUFFICIENT` (builder pre-check, informational) |
| Trip lifecycle | Fold | `TRIP_NOT_OPEN`, `TRIP_CLOSED`, `TRIP_NOT_CLOSED` |
| Reversals exist / match / not double | Fold | `REVERSES_UNKNOWN`, `REVERSES_MISMATCH`, `REVOKED_ALREADY`, `REVOKE_REFUNDED`, `REFUND_REVOKED`, `SALE_VOIDED` |
| Return / refund limits | Fold | `RETURN_LIMIT`, `REFUND_LIMIT` |
| Funds never negative; no fund for Credit | Fold | `FUND_NEGATIVE`, `NO_FUND` |
| Receipts/issues 1:1 with payment lines / sale lines | Fold `VerifyCrossChecks` on replay AND after every append | `INTEGRITY` (store maps `LedgerIntegrityException`) |
| Load number unique | Builder + DB UNIQUE | `DUP_LOAD`, `LOAD_NUMBER_REQUIRED` |
| Receipt number unique | DB UNIQUE | `DUP_RECEIPT_NUMBER` (store-level code — no fold code exists for it) |
| Idempotency key unique; second submit returns the SAME receipt reference, one sale | DB UNIQUE on the key row, written in the sale txn | duplicate → `FindCheckout` → same receipt reference, `Replayed: true` (not an error) |
| Nothing hard-deleted | No delete APIs, no FKs that cascade | C4 |

## 4. Arena record

Three whole-shape candidates ran in parallel (each read the full grounding brief and the key
sources; none touched a tracked file). Judged on the rubric below; screen against
design-red-flags (shallow module, information leakage, temporal decomposition, pass-through)
was applied at synthesis.

**Rubric (scored criterion by criterion):** R1 fold fidelity — reuses `LedgerState` for
guards and replay, no parallel SQL re-encoding, exact DomainError codes. R2 concurrency —
complete, correct lock discipline with the two-competitors interleaving; committed stock
never < 0. R3 mapping tradeoffs — honest table across all three shapes on the four mandated
axes; defensible verdict. R4 seam — bind-then-append preserved; use-case services are deep
modules; idempotency key → same receipt reference; print outside the sale txn;
InMemoryLedgerStore stays the test double; no Application → Infrastructure edge. R5 test plan
1:1 to docs/05 + lane mechanics with exact console streams. R6 safety — golden preserved; no
invented fold codes; no catalog tables; backup-before-migration; `-wal`/`-shm` intact.

| Candidate | Shape | R1 | R2 | R3 | R4 | R5 | R6 |
|---|---|---|---|---|---|---|---|
| **A** (base) | One `Movements` table, TEXT discriminator + normalized payload columns pinned by a generated CHECK matrix; hot `LedgerState` fold engine; five durable projection tables written from the fold clone in the same Append txn; `ILedgerStore` widened (8 × `Register*`, `Bind`, `Append(AppendBatch)`, `FindCheckout`, `RebuildAsync`) | 5 | 5 | 5 | 5 | 5 | 5 |
| B | One `Movements` table with a JSON payload column per sealed kind; fold-on-read with a watermarked in-process memo (SQLite is the only state); documents as normalized rows; `RunInTransaction<T>` widening; idempotency via deterministic SaleId | 5 | 5 | 5 | 4 | 5 | 5 |
| C | `Movements` spine + 16 per-kind payload tables (TPT) with typed document FKs; authoritative in-txn transient fold + five checked cache tables; per-operation connections; `CommitUnit(key, payload, audits)` | 4 | 5 | 5 | 4.5 | 5 | 5 |

**Base: A.** Reasons: (a) it is the only shape that satisfies the letter of docs/02 C5 —
"update balance cache" inside the checkout transaction, and C1 — `InventoryBalance` is a
rebuildable cache (A's projection tables are durable, disk-side, and rebuildable; B's memo
is in-memory and vanishes on restart, so Lane 6's "rebuilt **and cached** balances match
exactly" has no durable cached representation to compare after a restart); (b) it keeps the
brief's literal single-connection store and reproduces `InMemoryLedgerStore.Append` step for
step (same clone-apply, same VerifyCrossChecks, same codes), so the fold fidelity is
*behavioural identity*, not approximation; (c) its interface delta preserves the exact
BG-002 call shape (register → bind → append), which is what the scenario and every builder
test already express — the least churn with the deepest module (the use-case service
composes; the store commits); (d) R1-full compliance with zero deliberate fold-rule
re-encodings (C's five DDL mirrors and B's JSON codec are honest but extra surface).

**Grafts into the base:**

- *From B — durability and transaction mode.* `PRAGMA synchronous=FULL` (not NORMAL): a
  committed sale must survive power loss because the receipt prints after commit — WAL+NORMAL
  can roll back the newest commits on power loss, which would produce printed receipts for
  rolled-back sales. And: the Append write path must issue a literal `BEGIN IMMEDIATE`
  (raw ADO on the store's connection), because `Microsoft.Data.Sqlite.BeginTransaction()`
  emits a **deferred** `BEGIN` — under WAL a deferred txn that read before writing can hit
  `SQLITE_BUSY_SNAPSHOT` exactly in the two-writers race. EF stays the schema authority and
  migration owner; the store's prepared INSERT/SELECT statements target the EF-built schema.
- *From B — two BG-003 migrations* (`BG003_01_AddMovementLedger`, `BG003_02_AddCommitMarker`
  or equivalent split) so Lane 9's "prior schema fixture" is a real schema that already has a
  ledger and seeded movements to preserve across the upgrade — a single-migration plan cannot
  seed movements into a pre-movement schema.
- *From B — the watermark carries a documents version.* `LedgerHead(Id=1, LastSequence,
  DocumentsVersion)`: the staleness probe must detect document-only commits (a standalone
  `RegisterTrip` transaction advances no sequence but changes the bound document set), not
  just movement commits.
- *From B — `DebugOptions` test seam.* `FailNextAppendAfterInsert` / `DelayBeforeCommit`
  internal flags make Lane 3 (rollback) and Lane 8 (crash mid-transaction) deterministic at
  the unit level; the live lane still uses a real child-process kill.
- *From C — generic idempotency mapping.* The batch carries a generic key→payload pair
  (`Idempotency: new(Key, Payload)`) inserted in the sale txn with a UNIQUE PK; a duplicate
  returns `Replayed: true` and the stored payload (receipt reference) — the store stays
  use-case-agnostic and no checkout-specific member leaks onto `ILedgerStore` (A's
  `CheckoutIdempotency` renamed, `FindCheckout` retained as the lookup).
- *From C — constraint backstop mapping.* A `SqliteException` that bypasses the fold (raw
  SQL, manual edits, future features) maps: PK → `SEQ_ORDER`; CHECK → the fold code when
  determinable else `INTEGRITY`; UNIQUE → `DUP_RECEIPT_NUMBER`/`DUP_LOAD`; the fold almost
  always fires first with the exact code.
- *From C — one projection path.* Projections are written only from the validated fold clone,
  by one function shared by Append and Rebuild — never from SQL aggregates.
- *From C — L8 child handshake.* The crash-worker child signals readiness via a ready-file and
  runs with `stdio: ignore` (the harness's named-pipe boundary forbids piped capture), then is
  killed by PID.

**Rejections (with reasons):**

- *B's fold-on-read as the base* — the perf math is honest (warm op ~3-15 ms vs 500 ms; cold
  ~400-600 ms) and the memo is the same hot fold the base keeps, but B forfeits the durable
  balance cache docs/02 C5/C1 mandate and Lane 6's restart-meaningful cached comparison.
  Accepted instead: the base's hot fold (fast path) + durable projections (cache, rebuilt on
  restart ≤ 2 s).
- *B's deterministic SaleId idempotency* — derives the sale's identity from the checkout key,
  which couples identity to the key and fights the caller-supplied-id convention the scenario
  and tests rely on. The explicit key→payload row is restart-durable and debuggable (you can
  see which key produced which receipt on disk).
- *C's TPT* — 37 tables for no fidelity the CHECK matrix doesn't already give; the 16-table
  replay merge is the only shape that risks the 2 s rebuild rule; C itself called its five
  DDL mirrors of fold rules its honesty obligation.
- *C's per-operation connections* — the brief says "single connection"; the base keeps one
  long-lived connection per store and solves the restore window with an explicit
  store-closing hook. (Recorded as a documented alternative; the concurrency lane uses two
  store instances anyway, so per-op connections gain nothing for L5.)
- *A's own `synchronous=NORMAL`* — overridden by B's FULL (durability wins; one fsync per
  commit is inside budget).

**Dropouts:** none. **Convergence signal:** all three candidates independently chose
`BEGIN IMMEDIATE` + `busy_timeout` + WAL + sequence-assignment-inside-the-txn + the fold
guard inside the txn, and all three answered "replay through the real `LedgerState.Fold`" —
the lock discipline and the reuse-the-fold principle are the arena's consensus, not A's
opinion.

## 5. Decisions

| # | Decision | Verdict |
|---|---|---|
| D1 | **Idempotency key**: client-supplied branded `CheckoutKey(Guid)`; carried on the checkout command; a generic key→payload mapping row (payload = the receipt reference) UNIQUE in the DB, inserted in the sale txn. A duplicate submit is **not an error**: `Append(AppendBatch)` detects the key collision inside the txn, rolls back, and returns `Ok(AppendOutcome(Receipt: null, Replayed: true, ReplayedPayload: <stored payload>))`; the service returns the stored receipt reference (`CheckoutReference`), one sale. `FindCheckout` is the explicit lookup API for the service pre-check and cross-restart tests. Receipt NUMBER is a separate unique business ref (caller-supplied for BG-003; generation policy is a UI-era question). | C5 + lane 4 |
| D2 | **Lock/transaction ordering** (§6): raw `BEGIN IMMEDIATE` → read `LedgerHead(LastSequence, DocumentsVersion)` inside txn → refresh hot fold if stale → clone-apply guards → assign sequences → INSERT movements + bound documents + audit + key row, update projection tables, bump watermark → COMMIT. `PRAGMA journal_mode=WAL`, `synchronous=FULL`, `busy_timeout=5000`, `foreign_keys=ON`, `Pooling=False`, one long-lived connection per store. | C11, C15, lanes 4-5, 8 |
| D3 | **Caches live in SQLite** (five projection tables: cell balances, fund balances, observed, trip states, per-cell breakdown) updated in the same Append txn from the fold clone; the HOT `LedgerState` is the guard/report engine; movements are the source of truth; `RebuildAsync` recomputes projections via `Fold` and they must equal the running state exactly (Lane 6). | C1, C5 |
| D4 | **InMemoryLedgerStore stays** as the fast in-memory implementation of the widened `ILedgerStore`: Domain tests + in-memory `scenario two-peddlers` keep using it; SqliteLedgerStore is wired where persistence matters (host DI, Persistence tests, live lanes, `--persist`). Both implement the same bind-then-append contract; the open-attempt set is cleared at each append result (verified: no Domain test depends on retaining a failed attempt's documents). | C13, C14 |
| D5 | **Bound documents insert in the Append txn** (trips, sales+lines+payments, loads+lines, returns+lines, voids, adjustments, counts, closes). TransferRef/ReopenRef are document-less (id on the movement row only). **No DB-level FK from movement rows to document tables**: SQLite cannot conditionally FK one column to seven tables; document-less refs are legal; the fold's `DOC_KIND` + bound-document dictionaries + `VerifyCrossChecks` are the authoritative link checks, plus NOT NULL/CHECK on the ref columns. `RegisterTrip` commits its one row standalone (trips have zero movements by `TripBuilder` design — asymmetry documented on the interface). | C11, C12 |
| D6 | **Rebuild surface**: `Task<RebuildReceipt> RebuildAsync(CancellationToken)` on `ILedgerStore` — both stores implement (in-memory re-folds `_movements`); Infrastructure exposes it via `BentaGo.Tools ledger rebuild`. | lane 6 |

## 6. Lock discipline and the canonical interleaving (two competing checkouts of the last unit)

State: `stock(sardines@route) = 1`; two store instances S1, S2 — separate long-lived
connections, separate hot folds at `LastSequence = N`.

1. S1: literal `BEGIN IMMEDIATE` acquires the write lock (RESERVED at BEGIN, not at first
   write — this is why the append path is raw ADO, see §4 graft). S2's `BEGIN IMMEDIATE`
   blocks in the busy handler (`busy_timeout=5000`).
2. S1 in-txn: `LedgerHead` read = N, `DocumentsVersion` unchanged → no refresh; clone-apply:
   SaleIssue(1) → stock 0; `VerifyCrossChecks` ok; INSERT sale + movements + projections;
   head → N+m; COMMIT (durable: `synchronous=FULL`).
3. S2 unblocks, acquires the write lock; its in-txn reads see S1's committed rows.
4. S2 in-txn: head read = N+m ≠ hot N → **re-fold from Movements inside the txn** (stock 0,
   document set re-bound). S2's builder pre-check had passed against its stale context —
   that check is advisory; the fold is the authority.
5. S2 guard: `ApplyCommitted(SaleIssue(1))` → `CheckCell(0 − 1)` → **`STOCK_NEGATIVE`** →
   `ROLLBACK`, nothing inserted; S2's hot state is replaced by the re-folded state.

Why airtight: SQLite serializes writers (one write lock at a time under WAL), the write lock
is taken **before** the head read, and every Append runs its guards against a fold state that
includes every movement committed before the lock was acquired — the in-txn refresh closes
the stale-race, so two appends can never both validate against the same pre-commit state.
Same-key duplicates: the second INSERT violates the UNIQUE key row inside its txn → rollback →
`Append` returns `Ok(Replayed: true, ReplayedPayload: stored payload)` → the service returns
the first sale's receipt reference (D1; lane 4; `FindCheckout` is the explicit lookup for the
pre-check and restart tests). Committed stock never goes
below zero (lane 5). Deployment assumption recorded: never let an unvetted second writer
mutate the file with a deferred txn; the unique Sequence PK and re-fold-on-mismatch still
fail loud rather than corrupt.

## 7. Movement payload inventory (anchor of §8)

Every mapping candidate captures the same information per movement. The base shape — one row
per movement, kind discriminator + normalized payload columns — needs these nullable columns:

| Column | Kinds using it |
|---|---|
| `sequence` INTEGER (rowid PK, unique, monotonic) | all |
| `kind` TEXT (discriminator; enum NAME, not number — reorder-proof) | all |
| `when_utc` TEXT (ISO-8601 `'O'`, always `Z`) | all |
| `actor_id` TEXT (Guid `'D'`) | all |
| `doc_kind` TEXT + `doc_id` TEXT (NOT NULL; TransferRef/ReopenRef pairs are document-less) | all |
| `product_id` / `location_id` TEXT | 11 kinds (all stock/count movers) |
| `quantity` INTEGER (CHECK > 0) | the 10 stock movers (NOT CountObserved) |
| `sale_line_id` TEXT | SaleIssue, ReturnToStock |
| `payment_line_id` TEXT | Receipt, ReceiptRefund, ReceiptRevoke |
| `method` TEXT (PaymentMethod name) | Receipt, ReceiptRefund, ReceiptRevoke |
| `amount_centavos` INTEGER (CHECK > 0) | Receipt, ReceiptRefund, ReceiptRevoke |
| `reverses` INTEGER (target sequence) | SaleIssueRevoke, ReturnToStock, ReceiptRefund, ReceiptRevoke |
| `reason` TEXT | Damage, Loss, AdjustmentIncrease, AdjustmentDecrease, TripReopen |
| `trip_id` TEXT | TripClose, TripReopen |
| `observed_quantity` INTEGER (CHECK ≥ 0) | CountObserved |

13 nullable payload columns total; the widest row uses 7. A generated per-kind CHECK matrix
pins exactly the kind's columns as non-NULL (the domain's own sealed-kind rationale —
"every nullable field is an illegal combination" — carried into the schema). Rebuild reads the
table ordered by `sequence`, decodes via one exhaustive switch into sealed records, and hands
them to `LedgerState.Fold`.

## 8. Mapping tradeoff table — the three shapes

| Axis | **A — one row + normalized payload (chosen)** | B — JSON payload per kind | C — normalized table per kind (TPT) |
|---|---|---|---|
| Replay fidelity | Columns pinned per kind; CHECK rejects illegal combinations at write; the decode switch is the only reader; a hand-edit fails at read as `LEDGER_CORRUPT` | Fidelity = serializer fidelity; the schema knows nothing about payloads; a mutated blob commits silently and only explodes later as a coarse parse error | Schema mirrors records 1:1; strongest in principle — but every movement spans base + detail rows, and replay order needs a 16-table merge |
| VerifyCrossChecks impact | Replay = the real `LedgerState.Fold`; identical bound dictionaries to in-memory | Same fold; the JSON codec adds a second decoder on top of the fold switch | Same fold; the merge order is the new failure surface (a wrong interleave breaks SEQ_ORDER) |
| Migration churn | 1 table; new kind = new nullable columns + CHECK arm + decode case (1 migration) | New kind needs no migration (opaque blob) — its one genuine advantage — but the payload shape lives inside existing blobs | New kind = new table + mapping + fold switch; every change ripples across N tables |
| 100k-row rebuild perf | Single rowid-ordered scan; fold dominates (~110-120 ms as measured by BG-002); projection rewrite small | Same scan + JSON parse per row (~120-150 ms extra); still sub-2 s | N scans or a UNION; ~2× the read volume; the only shape that risks the 2 s rule |
| Integrity in the file | CHECK matrix + NOT NULL base + append-only triggers | None in SQL — legality lives only in .NET | Typed columns per table, but constraints are scattered across 16 tables |
| Analytics | Direct `SUM(Quantity)`, `WHERE ProductId`, `AmountCentavos` | `json_extract` or in-.NET parsing; fatter pages | Per-kind queries |

**Verdict: A.** B loses because a ledger whose rows can commit a mutated payload that only
fails later as a parse error is not a ledger — "replay fidelity" for B means "the serializer
round-trips what was written", not "the file cannot hold an illegal fact". C loses on
migration churn and rebuild cost: 16 tables for what one table plus one switch does, with no
fidelity gain the CHECK matrix doesn't already give.

## 9. Shape — how-to (recommended architecture)

### 9.1 The widened mutation seam (`src/BentaGo.Application/Contracts/ILedgerStore.cs`)

```csharp
public sealed record AppendReceipt(long FirstSequence, long LastSequence);
public sealed record RebuildReceipt(long LastSequence, int MovementCount, int DocumentCount);

/// Outcome of an AppendBatch: either the committed range, or — when the batch carried
/// an IdempotencyMapping whose key already exists — a replay (nothing was written this
/// call; ReplayedPayload is the payload stored by the first commit).
public sealed record AppendOutcome(
    AppendReceipt? Receipt,          // null when Replayed
    bool Replayed,
    string? ReplayedPayload);

/// The batch is the ONE durable write unit: movements + audits that must land in the
/// SAME transaction + an optional idempotency mapping (generic key -> payload).
public sealed record AppendBatch(
    IReadOnlyList<Movement> Movements,
    IReadOnlyList<AuditEvent>? Audits = null,
    IdempotencyMapping? Idempotency = null);

public sealed record IdempotencyMapping(string Key, string Payload);

public interface ILedgerStore
{
    LedgerContext Context { get; }
    LedgerState State { get; }
    IReadOnlyList<Movement> Movements { get; }
    IReadOnlyList<AuditEvent> Audits { get; }

    // register stages a document; Bind fold-binds the staged set; Append is the ONLY
    // durable writer and inserts staged documents in ITS transaction (C13).
    void RegisterTrip(Trip trip);      // trips are movement-less -> RegisterTrip is its own one-row txn (D5)
    void RegisterSale(Sale sale);
    void RegisterReturn(Return @return);
    void RegisterLoad(StockLoad load);
    void RegisterVoid(SaleVoid @void);
    void RegisterAdjustment(Adjustment adjustment);
    void RegisterCount(PhysicalCount count);
    void RegisterClose(DailyClose @close);
    void Bind();

    Result<AppendOutcome> Append(AppendBatch batch);          // the only transaction opener
    Result<AppendReceipt> Append(IReadOnlyList<Movement> movements);  // BG-002 call shape kept (scenario/tests)

    void RecordAudit(AuditEvent audit);                       // standalone tiny txn (lane-9 style)
    Result<CheckoutReference?> FindCheckout(string idempotencyKey);   // L4 lookup (see below)
    Task<RebuildReceipt> RebuildAsync(CancellationToken ct = default);             // L6
}

/// The receipt reference a checkout key maps to (D1, lane 4).
public sealed record CheckoutReference(string ReceiptNumber, SaleId SaleId);
```

Store-level error codes added (never fold-code collisions): `DUP_RECEIPT_NUMBER`,
`LEDGER_CORRUPT` (unknown Kind / illegal column set on read), `LOCK_TIMEOUT` (busy_timeout
expiry — retryable). **Idempotency duplicates are not errors by construction**: inside
`Append(AppendBatch)` the INSERT of the batch's key into `IdempotencyKeys` is the only
UNIQUE violation that is caught and turned into `Ok(AppendOutcome(Receipt: null, Replayed:
true, ReplayedPayload: <stored payload>))` (the batch is rolled back; nothing was written).
The service maps that to the stored receipt reference without a second lookup (D1, lane 4);
`FindCheckout` remains the explicit lookup API for the service's pre-check fast path and for
cross-restart idempotency tests (U9). All fold codes surface unchanged and verbatim.

### 9.2 The store (`src/BentaGo.Infrastructure/Persistence/`)

- `MovementRow` adapter entity (EF-native TPH on the 16 sealed records is impossible — they
  are immutable, `required init`, no parameterless ctor; the adapter confines EF to
  Infrastructure and doubles as the read-boundary legality check). Mapped to one `Movements`
  table per §7 with the generated CHECK matrix, TEXT `kind`/`doc_kind`/`method`
  discriminators, unique `sequence`, append-only triggers.
- Document tables (normalized, one per document kind + child line tables), projection tables
  (`CellBalances`, `FundBalances`, `ObservedQuantities`, `TripStates`, `CellBreakdown`),
  `AuditEvents`, `IdempotencyKeys(Key PK, Payload, SaleId)`, `LedgerHead(Id=1, LastSequence,
  DocumentsVersion)`.
- `SqliteLedgerStore : ILedgerStore`: one long-lived `SqliteConnection` (`Pooling=False`);
  pragmas per D2; a hot `LedgerState`; the §6 Append protocol; `RebuildAsync` (fold + rewrite
  projections + verify); `FindCheckout`; `RecordAudit`; restore close-hook (`RestoreService`
  gains a `Func<Task>? onPreRestore` invoked before the `-wal`/`-shm` moves).
- `DbInitializer` gains backup-before-migrate: when the DB exists and migrations are pending,
  `BackupService.CreateAsync()` first (C9, lane 9). Backup stays online via
  `BackupDatabase` against the WAL file; restore closes the store first.

### 9.3 Use-case services (`src/BentaGo.Application/Services/`, store-agnostic)

| Interface | Operation | One-txn contents |
|---|---|---|
| `IOpenTripService` | `OpenTrip(NewTripCommand)` | `RegisterTrip` (standalone row txn — trips have zero movements) |
| `ILoadService` | `Load(LoadCommand)` | `RegisterLoad` + `Bind` + `Append(batch, audit)` — load, lines, TransferOut+TransferIn, projections, audit |
| `ICheckoutService` | `Checkout(CheckoutKey, NewSaleCommand)` | key pre-check → builder → `RegisterSale` + `Bind` + `Append(batch, audit, idempotency)`; duplicate → same receipt reference, `Replayed: true`; print AFTER return (outside the txn) |
| `IVoidService` | `Void(VoidCommand)` | `RegisterVoid` + `Bind` + `Append(batch, audit)` |
| `IReturnService` | `Return(ReturnCommand)` | `RegisterReturn` + `Bind` + `Append(batch, audit)` |
| `IAdjustmentService` | `Adjust(AdjustmentCommand)` | `RegisterAdjustment` + `Bind` + `Append(batch, audit)` |
| `ICountService` | `Count(CountCommand)` | `RegisterCount` + `Bind` + `Append(batch, audit)` |
| `ICloseService` | `Close(CloseCommand)` | `RegisterClose` + `Bind` + `Append(batch, audit)` — expected cash computed by `State.TripCash` inside the txn |
| `IReopenService` | `Reopen(ReopenCommand)` | `Bind` + `Append(batch, audit)` — ReopenRef is document-less |

Every service is ~10 lines: build → stage → bind → append(+audit) → map the result. `Append`'s
transaction IS the use case's transaction; `Register*` is staging, so a service that stages
and then fails to build commits nothing (lane 3 is structural). **Print state lives outside
the sale transaction by contract**: `ICheckoutService` never prints; the caller renders the
committed document after `Append` returns `Ok` and writes print state through
`IPrintTicketStore` (Application contract; file/console implementation in Infrastructure;
hardware deferred) in its own tiny transaction — a failed print records `PRINT_FAILED` and
changes nothing about the sale (C5). Layering: Application references only Core + its own
contracts; nothing SQLite-shaped crosses the seam.

### 9.4 Rebuild

`RebuildAsync` = `BEGIN IMMEDIATE` → read movements ordered by sequence + the five fold-bound
document sets → `LedgerState.Fold` (BindDocuments → Validate/Apply → VerifyCrossChecks; any
violation throws `LedgerIntegrityException` → `INTEGRITY`, rollback, loud) → rewrite the five
projection tables from the fold's public API → bump `LedgerHead`. Invariant: after any
rebuild, `LastSequence == LedgerHead.LastSequence` and projections equal the hot state
exactly (Lane 6). `InMemoryLedgerStore.RebuildAsync` re-folds `_movements` — trivial, keeps
the contract.

## 10. File plan

- `src/BentaGo.Core/` — add `CheckoutKey` (branded Guid record struct, mirroring `Ids.cs`).
  No fold changes (single source of truth stays in Core).
- `src/BentaGo.Application/Contracts/` — `ILedgerStore` widened (§9.1), `AppendBatch`,
  `IdempotencyMapping`, `RebuildReceipt`, the nine use-case interfaces, `IPrintTicketStore`.
- `src/BentaGo.Application/Services/` — the nine services; `InMemoryLedgerStore` conforms to
  the widening (open-attempt cleared at append result); `TwoPeddlersScenario` drops the
  `(InMemoryLedgerStore)` cast (helpers re-typed to `ILedgerStore`; golden byte-identical).
- `src/BentaGo.Infrastructure/Persistence/` — `MovementRow` + mappings, two migrations
  (`BG003_01`, `BG003_02`), CHECK/trigger DDL, `SqliteLedgerStore`, projection upsert,
  `LedgerHeadAccessor`, `SqliteOpenOptions`.
- `src/BentaGo.Infrastructure/Backup/` — `RestoreService` close-hook; `DbInitializer`
  backup-before-migrate.
- `src/BentaGo.Infrastructure/Hosting/` — register `SqliteLedgerStore` (singleton) + services.
- `tools/BentaGo.Tools/Program.cs` — `scenario two-peddlers --persist [--data-root]`,
  `ledger rebuild`, `lanes bg-003 --head <sha>` (or `verify bg-003`), `lane8-crash-worker`.
- `tests/BentaGo.Tests/Persistence/` — §12 buckets + the schema fixture builder.
- `orchestrate/bentago/scripts/bg003-live.ps1` — lane runner (evidence = text snapshots
  named `bg-003-*.png`).

## 11. Implementation order (sequence of verifiable units)

p1 **Seam first, in memory**: widen `ILedgerStore`, align `InMemoryLedgerStore`, de-cast the
scenario. Gate: Domain suite green, golden byte-identical — the seam proves itself before any
persistence exists.
p2 **Mapping + migrations**: `MovementRow` + document/projection/`LedgerHead` entities, two
migrations (CHECKs, triggers, indexes), converters with a round-trip test over all 16 kinds.
p3 **SqliteLedgerStore**: connection/pragmas, `Append(AppendBatch)` per §6, staleness refresh,
projections, `RebuildAsync`, `FindCheckout`, `RecordAudit`, host wiring.
p4 **Use cases + tools**: the nine services, `IPrintTicketStore`, lane commands,
backup-before-migrate, restore close-hook.
p5 **Persistence tests + lanes + perf**: the nine buckets; `bg003-live.ps1`; **baselines
FIRST** (trunk empty-txn median, BG-002 in-memory scenario time), then P95/rebuild probes.
p6 **Audit + merge**: independent ledger/transaction/migration/recovery verdicts at head SHA
→ rebase onto main, prove patch unchanged (diff SHA) → operator squash-merge.

## 12. Test plan (1:1 map to docs/05, `tests/BentaGo.Tests/Persistence/`)

All buckets use real SQLite files in fresh temp roots; `dotnet test -c Release --filter
Persistence`. The `DebugOptions.FailNextAppendAfterInsert`/`DelayBeforeCommit` seam makes the
failure-injection buckets deterministic; the migration fixture is a *code-built* DB at
migration 01 with seeded movements (reproducible from a fixture builder, not a checked-in
binary).

| # | Bucket | Maps to | Proves |
|---|---|---|---|
| U1 | `RollbackTests` | docs/05 unit + L3 | batch whose last movement fails the fold → zero rows in every table, projections untouched, no sequence gap; forced mid-txn failure via the seam → nothing partial |
| U2 | `ConcurrencyTests` | L5 | two store instances, one file, barrier-synchronized, race the last unit → exactly one Ok, one `STOCK_NEGATIVE`, committed stock == 0; `MIN(quantity)` over the stream ≥ 0 |
| U3 | `UniqueKeyTests` | L4 + docs/02 | same key twice → one sale, same receipt reference; duplicate receipt number → `DUP_RECEIPT_NUMBER`; duplicate load number → `DUP_LOAD` |
| U4 | `RebuildTests` | L6 | persisted two-peddler day; `RebuildAsync` → state + projections identical to pre-rebuild; `LedgerHead.LastSequence` == stream max |
| U5 | `CloseLockTests` | L9 close half | close via one store, open a second store over the same file → post-close sale fails `TRIP_CLOSED`; reopen → sale passes |
| U6 | `MigrationTests` | L9 | fixture at migration 01 with seeded movements → migrate to head → backup file exists first, totals preserved, rows unchanged |
| U7 | `BackupTests` | L9 backup half | online backup against the live WAL store → quick_check ok, SHA stable, backup render matches |
| U8 | `RestoreTests` | L10 | restore with the store closed → re-opened store renders exactly the pre-upgrade report; `-wal`/`-shm` moves + rollback match BG-001 expectations |
| U9 | `IdempotencyTests` | L4 | through `ICheckoutService`: same key twice (same instance), same key across a fresh store (restart), concurrent same key → one sale, identical receipt references every time |

Plus a golden test: persisted scenario render == in-memory golden byte-for-byte, and a
rebuild == cached assertion on every balance surface (stock/funds/observed/trips/breakdown
per cell).

## 13. Live-lane mechanics

Each lane runs `BentaGo.Tools` under a **fresh** temp data root (per-lane dir
`bg-003-l<n>-<guid>`; `BENTAGO_DATA_ROOT` or `--data-root`), deleted on exit. Artifacts:
`artifacts/verification/bg-003/<head>/bg-003-<slug>.png` — **UTF-8 text snapshots** of the
console (BG-002 precedent: text evidence; the `.png` name is the docs/05 contract; no
image-capturing model). Console contract: one `L<n> <slug>: PASS|FAIL (<reason>)` line per
lane to **stdout** (diagnostics to stderr), then `LANES: 10/10 PASS`; exit 0 iff all lanes
pass. Byte-identical on a pinned host (fixed clocks, deterministic ids).

| Lane | Driver | PASS condition | Evidence |
|---|---|---|---|
| L1 stock load | persisted scenario up to the load-out | both locations reconcile; every movement reconciles | render + per-cell Σ |
| L2 checkout | full persisted run + row-presence checks | sale, lines, payments, movements, projection, audit commit together; render == golden | render + counts per table |
| L3 rollback | deliberately failing append (+ seam) | zero partial rows in all tables | row counts |
| L4 idempotency | same key, two `Checkout` calls | one sale; both returned the same receipt reference | references + sale count |
| L5 concurrency | two store instances / processes race the last unit | one Ok, one `STOCK_NEGATIVE`; committed stock == 0 | result codes + balances |
| L6 rebuild | persisted run → `ledger rebuild` → capture again | post-rebuild == pre-rebuild exactly | both renders + receipt |
| L7 reversals | persisted run through void + return | original rows remain; reversals reconcile | counts + render |
| L8 power loss | `lane8-crash-worker` child (ready-file handshake, `stdio: ignore`) killed by PID mid-txn | reopen + rebuild: full txn or none; `quick_check` ok | movement count + quick_check |
| L9 migration | fixture at migration 01 → `foundation migrate` | history at head; totals preserved; **backup file created first** | migration ids + totals + backup SHA |
| L10 restore | restore the L9 backup over the upgraded DB | re-opened store renders == pre-upgrade fixture | renders + SHA-256 |

Perf gate (not a lane): baselines FIRST (trunk empty-txn median, BG-002 scenario median);
then a seeded file with 10,000 distinct `ProductId` GUIDs and 100,000 movements — P95
checkout ≤ 500 ms (single-writer path = one `LedgerHead` probe + clone-apply + inserts, no
re-fold), rebuild ≤ 2 s (rowid scan + decode + ~120 ms fold + projection rewrite with
headroom).

## 14. Risks

- R1 **Restore-while-connected**: `RestoreService` swaps the DB file behind a path; the
  long-lived ledger connection must be disposed/reopened around restore (the `onPreRestore`
  hook; lane 10 exercises it). Per-operation connections (C's shape) avoid this but forfeit
  the warm connection — recorded alternative, rejected for the single-connection contract.
- R2 **Pre-migration backup deadlock**: `DbInitializer` must run the backup on a SEPARATE
  short-lived connection before `MigrateAsync`, never on the store's connection; skip the
  backup when no DB file yet exists (fresh root) or no migrations are pending.
- R3 **Hot-fold staleness across processes**: the `LedgerHead` probe must run INSIDE the
  txn (after `BEGIN IMMEDIATE`), not before — checking before the lock reintroduces the race
  §6 closes. `DocumentsVersion` catches doc-only commits (`RegisterTrip` standalone).
- R4 **100k-row rebuild ≤ 2 s**: materialization must be column-pruned and rowid-ordered;
  validate early with the perf probe (p5) before optimizing the mapping.
- R5 **DateTimeOffset TEXT ordering**: only UTC (`Z`) is ever stored; never store offsets —
  ISO-8601 `'O'`/`Z` text sorts correctly for replay joins.
- R6 **InMemoryLedgerStore open-attempt clearing** is a behavior change; the Domain suite
  (43 tests) is the p1 regression gate.
- R7 **Perf-harness warm-up policy**: the P95 must sample after one warm checkout (the cold
  first touch after open includes the one-shot full re-fold); record the decision with the
  baseline, and note a startup eager-fold as the alternative if even the first op must be
  ≤ 500 ms.
- R8 **TEXT discriminators**: enum *names* not numbers — adding a kind between existing
  enum values must not renumber old rows; the decode switch is the single reader.
- R9 **RegisterTrip asymmetry** (standalone one-row txn) is a documented interface surprise;
  the alternative (trips riding a later append) resurrects the deferral problem — kept, and
  called out for the audit.

## 15. Open questions the implementer must decide first

1. Does the widening of `ILedgerStore` (8 × `Register*` + `Bind`) belong on the interface
   itself, or on a second registry interface the scenario types against, to keep
   `ILedgerStore` a pure mutation seam? (Interface-depth call; the base puts them on the
   interface because the scenario and every builder test already call them in that shape.)
2. `RegisterTrip` as its own one-row transaction (asymmetric) vs. forcing trips to ride a
   later append — the base accepts the asymmetry (D5); flag for the audit.
3. Receipt-number generation policy (per-trip per-day sequence? global?) is out of BG-003
   scope — caller-supplied with UNIQUE enforcement; confirm the usage services do not need to
   generate numbers for lane 4's "same receipt reference" wording.
4. `busy_timeout` = 5000 ms default and the `LOCK_TIMEOUT` retry policy (auto-retry once vs
   surface) at the WPF layer — correctness never depends on it; availability UX only.
5. `CheckoutKey` placement: a branded type in `BentaGo.Core` (mirrors `Ids.cs`) vs. an
   Application-level brand vs. a plain string — the base recommends Core branding.
6. The perf harness's warm-up policy (R7): sample P95 after one warm checkout and document
   it, or pay a startup eager-fold — decide when the baseline is recorded.
7. `AuditEvent.Sequence` semantics on persisted audits (store the value as given, including
   the scenario's −1, vs. reassigning) — cosmetic for the golden; a reports-slice question.

## 16. Next implementation step

p1 of §11: widen `ILedgerStore` with the Register*/Bind contract, align
`InMemoryLedgerStore` (open-attempt cleared at append result), remove the scenario cast, and
re-run the Domain suite + golden — the seam must prove itself in memory before any
persistence code exists.