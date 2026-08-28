# BG-002 Synthesis — "The Ledger Is the Entity" (base: runner A, grafted)

## Synthesis decision
**Base: Runner A (ledger-first).** Why: docs/02's locked contract says the append-only movement ledger is the source of truth and InventoryBalance is a rebuildable cache — A's shape *is* that contract, with state as pure folds and each invariant encoded exactly once. B's state-as-type aggregates (TripClosed has no Sell) give compile-time rejection but re-introduce state beside the ledger: transitions return bundles that must agree with the fold, so every cross-cutting rule (return limit, closed-trip guard, location ownership) would be encoded twice. B's aggregates also cost a per-state persistence mapping switch in BG-003 for no payoff the fold doesn't already give (A's `Accepts`/`Fold` reject the same lanes, with a structured DomainError instead of a compile error — equally deterministic for the lanes).

**Grafted from runner B:**
- Draft/commit discipline naming (B's `SaleDraftBuilder` ≈ A's builders; keep A's builder surface).
- Single-ledger-writer: only `ILedgerStore.Append` mutates; aggregates/builders are pure and take the current read model as a parameter (A already does via `LedgerContext`).
- Deterministic `SegmentIdSource` + fixed `ScenarioClock` naming for the scenario (A's "fixed ids + injected clock" made concrete).

**Grafted from neither / coordinator overrides:**
- **Sealed per-kind movement records** instead of one enum record (overrides A's tradeoff #1). `abstract record Movement`; 16 sealed kinds; each payload is exactly its kind's fields — no nullable ProductId/Method/Reason quadrants, no `with`-able illegal states. The fold is one exhaustive `switch` expression per axis (stock/effect/funds/state); adding a kind is a compile-forced fold update (type-system-discipline; the audit reviewers' favorite standard).
- **`ReceiptRefund` kind added** (funds out on returns that refund cash) — a gap both runners missed: A's ReturnToStock is stock-only, but a partial customer return paying out cash is a funds event ("explains every cash variance" needs this).
- **Ids: Guid-backed branded ids** (docs/02 line 42: "internal UUID" is mandatory, locked). Deterministic scenario: `ScenarioIdSource(seed)` derives stable Guids (SHA-256 of seed+segment, first 16 bytes, version/format set to v4-like) — byte-identical across runs. Human tokens (`R-000001`, `Load-LD-0001`) are rendered separately by `DocumentRef`.
- **Funds postings live in the ledger** (A's bet kept): Receipt/ReceiptRevoke/ReceiptRefund are movements; expected-cash and credit-exclusion are folds. The locked doc calls it the inventory ledger, but the same doc requires cash-variance explanation and one source of truth — postings are the only shape that satisfies both. Decision row recorded.
- **No AccountsReceivable fold in BG-002.** Credit = payment line with Reference, excluded from physical cash by the funds fold. Per-customer AR (utang tracking) needs the Customer entity, which BG-002's Files list does NOT include — later slice (BG-004/BG-005 era). Decision row recorded.
- **Scenario script ships in BentaGo.Application/Scenario** (A + B converged): one source for Tools + Tests; docs/05's "tests/Domain" line covers the invariant tests + golden render assertion, not the runnable script. The tool never references tests.
- **Close is per-trip** (A's shape: DailyClose document + TripClose movement per trip; trip = one day's route). Lane 9's closed-trip rejection is the fold guard + ReopenBuilder (admin Reason required).
- **Voided-sale funds are excluded from revenue/expected cash by construction**: the void appends SaleIssueRevoke + ReceiptRevoke; the funds fold cancels the receipt's effect; revenue reads = sum over non-revoked receipts (no void flag needed anywhere).

## Resulting shape (one paragraph)
Append-only `Movement` stream (16 sealed kinds: OpeningStock, TransferOut, TransferIn, SaleIssue, SaleIssueRevoke, ReturnToStock, Damage, Loss, AdjustmentIncrease, AdjustmentDecrease, Receipt, ReceiptRefund, ReceiptRevoke, CountObserved, TripClose, TripReopen) is the model center. `LedgerState.Fold` replays the stream into balances: StockOnHand(product, location), FundsOnHand(fund), InventoryBreakdown (exact CSV columns), ObservedUnits, TripState, SaleState, Returned(line) (cumulative-return limit). Documents (Trip, Sale, SaleLine, PaymentLine, SaleVoid, Return, ReturnLine, StockLoad, StockLoadLine, Adjustment, PhysicalCount, CountLine, DailyClose, AuditEvent) are immutable fact snapshots grouped by typed DocumentRef; status always derived, never stored. Builders are pure `(LedgerContext, Command) → Result<Draft>` validating the full precondition set before any movement exists (lane 5 = zero movements on failure, structurally). `ILedgerStore` (Context, State, Append, RecordAudit) + `InMemoryLedgerStore` in BentaGo.Application; BG-003 swaps the SQLite implementation behind the same contract. `TwoPeddlersScenario` in Application/Scenario runs on pure folds with ScenarioIdSource + fixed clock — `tools/BentaGo.Tools scenario two-peddlers` prints ordered events + exact balances, byte-identical re-runs (lane 10), no SQLite/EF/files anywhere in the path.

## Invariant encoding map (final)
| Invariant | Where |
|---|---|
| Money = integer centavos, no floats | `Money(long)` checked ops; `FromPesos` only parse, exact decimal, rejects >2dp |
| Integer non-negative quantities | `Quantity.Create` rejects negatives |
| Payment sum == sale total (split ok; underpaid refused pre-event) | `SaleBuilder.Build` → `PAYMENT_TOTAL`, zero movements; fold re-verifies Σ receipts | 
| Return limit per line ≤ sold | `ReturnBuilder` vs `State.Returned(line)` fold; fold re-checks |
| Reversals point at originals; both records preserved | `Reverses` required by revoke/return factories; fold validates target kind + existence |
| Stock ownership (wrong peddler location) | `SaleBuilder`: cmd.Trip.Location == sale location |
| No negative stock | `Accepts` + `Fold` reject cell<0 |
| Closed trip rejects mutation until reopen | `TripState` fold + `Accepts` guard; `ReopenBuilder` admin Reason |
| Append-only, monotonic sequences | Store assigns; BG-003 unique index + single transaction |
| Checkout commits, print after; print failure never changes sale | `CheckoutService` ordering + audit event |
| Storage UTC / display Manila | factories require WhenUtc.Kind==Utc; `ManilaDisplay` pure conversion |
| InventoryBalance rebuildable | `Fold` replay; store State = cached fold |

## Tradeoffs accepted
- Sealed 16-kind records: more types than the enum single-record, one exhaustive fold switch — in exchange for unrepresentable illegal movements and compile-forced fold updates.
- Funds in the ledger despite "inventory ledger" wording — accepted as the only shape that explains every cash variance from one source (decision row).
- Guid ids (docs/02 locked) with scenario seed-derived determinism — accepted in exchange for doc compliance + global uniqueness; human tokens rendered separately.
- Documents snapshot commercial facts, movements carry effects (two movements per location move) — duplication accepted in exchange for immutable receipts and plain per-cell sums; fold cross-checks document totals vs movement sums so drift is loud.

## Alternatives considered (synthesis level)
- B's state-as-type aggregates: lost because state duplicates the ledger and cross-cutting invariants get two encodings; compile-time rejection replaced by equal-behavior structured DomainError from Accepts/Fold.
- Single generic double-entry (no kinds): lost — counts/closes/observations are not debits/credits; provenance untyped.
- EF entities + stored status: lost — status drift from ledger, the exact bug the rebuildable-cache language guards.

## Open questions
- None blocking. BG-003 mapping of 16 sealed kinds (EF TPT/TPH decision) is BG-003's architect pass.

## Next implementation step
Phase p1: Core `Result<T>`, `DomainError`, `Money`, `Quantity`, branded Guid ids, `PaymentMethod`, `Fund`, `MovementKind`-derived sign — with tests. Then p2 movements + fold; p3 documents + builders; p4 store + scenario + tool; p5 tests. Content-exact authoring per the proven pattern.
