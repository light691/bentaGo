# BG-006 Architecture — Offline POS Sales Domain

**Unit:** docs/05 lines 341–393 (BG-006: complete offline sales and payments). **Depends on:** BG-005 merged (main). **Review gate:** interaction-changing — stops at merge-ready for operator review.
**Phase A grounding:** docs/00–05, docs/decisions/0001, docs/designs/bg-004-synthesis.md, docs/hardware-profile.md, and the live code in `src/BentaGo.Core|Application|Infrastructure` + `src/BentaGo.App` + `tests/BentaGo.Tests` (all read; facts below are from the code, not the briefs).
**Arena:** Candidate A (extend-the-seams: `CheckoutOrchestrator` + `ISaleQueryService` + `IProductStore` extension) = base; grafts from B (`CheckoutSession` exactly-once typing, 20/20 discount math, live stock checks) and C (`IX_PaymentLines_Reference`, `BANK_REFERENCE_REQUIRED`). Cross-judge: A 51 / B 49 / C 41 — base confirmed. See [Synthesis decision](#synthesis-decision).

## Problem

BG-006 turns the existing sale WRITE path into the full offline POS flow: keyboard-HID scan + manual search, cart, allowed discounts, Cash/GCash/Maya/Bank Transfer/Credit plus split payments, idempotent checkout, a saved-sale state shown before any print attempt, and transaction history that survives an offline restart. A complete sale seam **already exists end-to-end in the codebase**: `SaleBuilder.Build(LedgerContext, NewSaleCommand)` produces `SaleDraft(Document, Movements)`; `ICheckoutService.Checkout(CheckoutKey, NewSaleCommand)` (implemented in `UseCaseServices.cs`) runs build → `RegisterSale` → `Bind()` → `Append(new AppendBatch(movements, Audits, Idempotency))` — one transaction on `SqliteLedgerStore`'s single long-lived connection, with store-level idempotency replay (`FindCheckout` + in-txn key check) returning the SAME `CheckoutReference` on a duplicate key. What does NOT exist is the caller surface: nothing resolves a live sale from the session actor, nothing generates a receipt number, product lookup has no barcode/name path (only `FindBySkuAsync`/`GetAllAsync`), nothing reads sale history back (the App shows a simulated banner; `ScreenKeys.Transactions` is parked), and the App's cart is unpacked VM state.

The shape is non-obvious because of four hard constraints: **(1)** cross-connection atomicity is impossible (the F3 lesson: `LoginService` compensates because an EF write and a ledger append cannot share one transaction) — every durable write must ride the ledger store's `Append` transaction or not exist; **(2)** `TwoPeddlersScenario` sells with hard-coded receipts `R-000001..4` and its rendered golden is BYTE-STABLE (fresh in-memory run == SQLite run == after reopen == after rebuild; `LaneRunner.Lane2` pins sales==4 / lines==4 / payments==5 / movements==28; tools' GOLDEN_IDENT/RESTART/REBUILD checks pin the same) — the design must touch zero pinned surfaces; **(3)** `CheckoutService`, `SaleBuilder`, `ILedgerStore`, and `InMemoryLedgerStore` are constructed directly by scenario/lane/tool/test code — mutating their shape or semantics reopens the golden blast radius; **(4)** money is integer centavos (`Money(long Centavos)`), prices are locked on `Product.RetailPrice` (0001-4), discounts are permitted only per 0001-5, and printing is BG-007's job — BG-006 only defines the saved-sale state shown before any print attempt.

The answer: a **`CheckoutSession`** (Application layer) — a session-scoped object that owns the `CheckoutKey` (exactly-once as a typed invariant), the cart lines, payment assembly (split included), the discount set, actor/trip derivation, and the `SavedSaleState` outcome — over the UNCHANGED `ICheckoutService` seam; a read-only `ISaleQueryService` over the store's hot `Bind()`+`Context/State/Audits` surface (the proven `TripReadService`/`AdminReadService` pattern); an `IProductSearchService` over a two-method `IProductStore` extension; and one additive migration — indexes only, no tables, no counter table.

## Usage (caller's view)

Written before the types; the shape in [Shape](#shape) is derived from these call sites and reconciled to them.

README-style quickstart:

```
Selling = one CheckoutSession per sale. The session owns the CheckoutKey, the
cart, the discount policy, and the ledger commit; the view model mirrors it for
binding. The UI never builds a NewSaleCommand, never sees a store, and never
touches a receipt format. After Submit() succeeds, the VM binds the returned
SavedSaleState — the saved-sale state shown BEFORE any print attempt (printing
is BG-007; BG-006 never calls a printer). Product search and transaction
history are separate read-only services.
```

Call site 1 — the POS screen (scan → cart → F7 discount → F12 payment → submit):

```csharp
// SalesViewModel (retained screen). Session created lazily on first cart action.
Result<ICheckoutSession> created = await _sessions.CreateSessionAsync(ct);   // actor → peddler → open trip
if (created.IsFailure) { /* AUTH_ACTOR_NOT_SIGNED_IN / NO_OPEN_TRIP ... corrective copy */ return; }
ICheckoutSession session = created.Value;

// scan Enter (keyboard-HID text; CR suffix trimmed at the search boundary):
session.AddProduct(await _search.ScanAsync(scanText, ct), Quantity.Of(1));   // Result; STOCK_INSUFFICIENT if qty > on-hand
// F7 on the selected line (permitted set only):
session.ApplyDiscount(session.Lines[selected].LineId, DiscountType.Senior);  // DISCOUNT_NOT_ALLOWED for un-flagged products
// F12 → payment dialog (unchanged modal; Esc → null → cart untouched):
PaymentDialogResult? result = await _dialogs.ShowAsync(payment, focusAfterClose: AutomationIds.SalesScanBox);
if (result is null) return;
session.SetPayment(result.Lines, result.Change);
// duplicate Enter is structurally inert: Submitting gate + disabled IsDefault confirm
// + session-owned key → FindCheckout replay. One sale, one receipt, always.
Result<SavedSaleState> saved = await session.SubmitAsync(ct);
if (saved.IsFailure) { /* STOCK_INSUFFICIENT / PAYMENT_TOTAL / DUP_RECEIPT_NUMBER-retry-exhausted:
                          cart and payments stay intact, SAME key on retry (lane 9) */ return; }
_receipt = new ReceiptState(saved.Value, PrintAttempted: false);
// → "Sale saved — Receipt R-20260115-004" + payment summary + "Print pending — printer needs
//   attention" copy. Print/Reprint/New Sale/View Sale actions (Print/Reprint inert = BG-007).
```

Call site 2 — product search + transaction history (F3 search; Alt+4 Transactions screen):

```csharp
// search: SKU → barcode → name, all resolving the same product; OnHand from the movement fold.
IReadOnlyList<ProductHit> hits = await _search.SearchAsync(query.Trim(), limit: 25, ct); // hit.OnHand = live stock
// history (lane 10 offline restart; lane 5 bank-reference searchable):
Result<IReadOnlyList<SaleHistoryRow>> page = _queries.Query(peddler, new SaleHistoryFilter(
    Day: ManilaDay.Today(_clock), Trip: null, Receipt: null,
    Method: PaymentMethod.BankTransfer, Status: SaleStatus.Committed, Reference: "BDO-998877"));
Result<SaleDetail> detail = _queries.GetByReceiptNumber(peddler, "R-20260115-004");   // one sale only
```

Call site 3 — deterministic test (no login; explicit actor harness; real components):

```csharp
var actors = new ActorContextHarness();               // IActorContext fake: CurrentUserId settable
actors.Set(userId);                                   // peddler-linked user; open trip + stock seeded
var session = new CheckoutSession(store, products, checkout, new FixedNumberSource(["R-T1"]), actors, actors, () => FixedUtc);
session.AddProduct(sardines, Quantity.Of(5));
session.SetPayment([new PaymentLineDraft(PaymentMethod.Cash, Money.FromPesos(225m), "")], Money.Zero);
Result<SavedSaleState> saved = await session.SubmitAsync(ct);   // "R-T1", PrintStatus.Pending
```

What the caller never touches: `CheckoutKey` ownership, receipt derivation, replay handling, `DUP_RECEIPT_NUMBER` retry, actor derivation, price authority (product record, 0001-4), discount percent/rounding, reference policy, and change math.

## Shape

### The loaded base

The sale WRITE path is **unchanged**: `SaleBuilder` rule set (lines non-empty, dup ids, qty > 0, discount ≥ 0, line total ≥ 0, `STOCK_INSUFFICIENT` pre-append, payments total == sale total `PAYMENT_TOTAL`, credit reference `CREDIT_REFERENCE_REQUIRED`, cash `TENDER_INSUFFICIENT`/`CHANGE_MISMATCH`), `ICheckoutService.Checkout`, `ILedgerStore`, `AppendBatch`, `FindCheckout`, `InMemoryLedgerStore`, and `SqliteLedgerStore` — **all byte-behavior-identical**. BG-006's new durable surface is exactly three indexes in one additive migration. `Append` remains the one writer; nothing new joins a transaction; the F3 lesson is never violated.

### Load-bearing decisions

**D1 — `CheckoutSession` is the deep module; exactly-once is a typed invariant, not a convention.**

One session per sale, created from the session actor; the object owns:

- **The `CheckoutKey`** (branded, exists) — minted ONCE at construction and reused on every attempt. Duplicate Enter, retry after failed persistence, and post-crash re-ask all present the SAME key: `FindCheckout` replays the same `CheckoutReference` and the session returns the identical `SavedSaleState` (replayed sale re-fetched from the committed `Sale` via `FindSale`). "New Sale" is the only key change, and it is a deliberate new session. `SubmitAsync` is gated: `Building` only; while `Submitting` a second call no-ops; `Saved` is terminal.
- **The cart lines** (immutable snapshot at scan time: `ProductId`, `Sku`, `ProductName`, `UnitPrice` from the Product record — 0001-4 locked price, never caller-supplied — `Quantity`, `Money Discount`, `DiscountType`); Dedupe by `ProductId`, re-scan increments.
- **Payment assembly** (`SetPayment`): `PaymentLineDraft[]` → `NewPaymentLine[]` with the `Total` validation the builder re-checks in-txn. Single **Cash** ⇒ `Tendered = Amount + Change` (change only on the cash component); **Gcash/Maya/BankTransfer** ⇒ `Amount == Total`, `Tendered = Amount`, `Change = 0`; **Credit** ⇒ `NewPaymentLine.Credit` helper, reference required; **Split** ⇒ one `NewPaymentLine` per draft (funded methods only), sum must equal `Total` (dialog balance-zero gate + builder `PAYMENT_TOTAL` backstop); split-cash lines are exact-tender (`Tendered == Amount`, no per-line tender entry — conservative, documented).
- **Actor + capability** (D3 below), **discount policy** (D4), **references** (D5), **receipt number** (D6), **replay/DUP retry** (D6), and the **`SavedSaleState`** outcome (D7).

The App flows through ≤3 files to trace a sale: `SalesViewModel → CheckoutSession.SubmitAsync → ICheckoutService.Checkout → ILedgerStore.Append`. The session is NOT a pass-through: it owns policy (key discipline, actor, price authority, discount set, reference policy, payment normalization, receipt recovery) that no other layer holds.

**D2 — Transaction atomicity: the ledger connection is the only writer; everything new is read-only or an index.**

Checkout stays on `Append(AppendBatch)` — sale row, lines, payments, movements (`SaleIssue` per line + `Receipt` per non-Credit payment), `SALE` audit, idempotency row, and projections commit in ONE `BEGIN IMMEDIATE` transaction (existing). Power loss during submit ⇒ all or nothing (lane-8 machinery). New reads (`ISaleQueryService`, `IProductSearchService`) run on the store's hot fold / EF short-lived contexts — reads need no cross-connection transaction (F3 applies to writes, not reads; WAL gives snapshot semantics). The one migration writes no data.

**D3 — Actor binding lives in the session, not in `CheckoutService` (F8/BG-005d pattern, byte-stable seam).**

`CheckoutService` is constructed directly by `TripLifecycleScenario`, `LaneRunner`, `PerfRunner`, and persistence tests with caller-supplied `By`; binding that class to the singleton `IActorContext` would force context seams through every harness and reopen goldens (Candidate C's flaw). Instead the session factory derives everything from the session actor:

- `ActorGuard.ResolveAsync(IActorContext, IUserAccountStore)` — `AUTH_ACTOR_NOT_SIGNED_IN` (no `CurrentUserId`) / `AUTH_ACTOR_UNKNOWN` / `AUTH_ACTOR_DEACTIVATED` / `AUTH_ACTOR_MISMATCH` (context changed between create and submit) — the exact `UserAccountService` guard set.
- Peddler + trip + location derive from the actor's `User.PeddlerLink` and its OPEN trip (`NO_PEDDLER_LINK`, `NO_OPEN_TRIP`); the sale never names a trip from the UI. Every `NewSaleCommand` carries `By = session actor`; the `SALE` audit already records `Terminal: Environment.MachineName` (F12 convention).

**D4 — Discounts: a typed permitted set with centavos-exact math, pure in Core.**

`DiscountType { None, Pwd, Senior }` is the closed permitted set (0001-5: nothing else exists; F7 cycles None → Pwd → Senior → None, one type per line, never stacked). `DiscountPolicy.Allowed(product, type, rates, unitPrice, quantity)` is a pure Core function: allowed iff `None` (zero) or the product's `SupportsPwdDiscount`/`SupportsSeniorDiscount` flag matches the type and the rate is positive; amount = `floor(unitPrice.Centavos × qty × percent / 100)` — integer truncation toward zero, deterministic, never negative, exact for whole-peso statutory math (4500 × 20 / 100 == 900). Rates default to **statutory 20/20** (PWD RA 10754, Senior RA 9994); the rate record is boundary-level, NOT in the model (0001: percentages live in the CSV import preview; a Settings-backed override is a documented follow-up, see Open questions). `SaleBuilder` gains ONE invariant rule: `DISCOUNT_TYPE_MISMATCH` — `(DiscountType == None) != (Discount.Centavos == 0)` — enforced per line; the scenario passes `DiscountType.None` + zero on every line, so it stays byte-stable. Discount money persists in the existing `SaleLines.discount_centavos` column; the type is not persisted to schema (derivable from flags + policy at read time; a `discount_type` column is a cheap later migration if BG-008 needs per-type reporting).

**D5 — References: bank REQUIRED at the domain boundary; wallets captured and persisted at the dialog.**

`SaleBuilder` gains `BANK_REFERENCE_REQUIRED` (non-blank `Reference` for `PaymentMethod.BankTransfer`, mirroring `CREDIT_REFERENCE_REQUIRED`) — docs/05 lane 5 "reference is required and searchable" is a document-validity rule; the scenario has no bank lines, so it stays byte-stable. Gcash/Maya: the payment dialog captures and persists the wallet reference (lane 4 "references persist") but does not hard-require it (client policy question, see Open questions). Lane 5's "searchable" is served by `ISaleQueryService.Query(Reference=...)` (contains, OrdinalIgnoreCase) and the new `IX_PaymentLines_Reference` index makes the equivalent SQL path scale-proof for exports.

**D6 — Receipt numbers: `R-{yyyyMMdd}-{NNN}`, day-global count-derived, overridable, UNIQUE-backstopped.**

`ISaleNumberSource.Next(nowUtc)` binds the store and counts committed `Sales` whose `ReceiptNumber` starts with the Manila-day prefix (`R-{yyyyMMdd}-`), returning `R-{date}-{count+1:D3}`. Day-global scope matches the GLOBAL `Sales.receipt_number` UNIQUE index (a per-trip counter would collide across peddlers — Candidate C's flaw). The UNIQUE constraint is the collision backstop: a `DUP_RECEIPT_NUMBER` append failure (whole transaction rolled back, idempotency row never written) re-counts and retries under the SAME key at most once; a second collision surfaces as-is (astronomically rare under `BEGIN IMMEDIATE` single-writer serialization). Gaps are accepted (docs/01 requires uniqueness, not gaplessness). The source is injectable: the scenario and lane tests never call it (they keep explicit `R-000001..4`), so goldens stay byte-stable; `FixedNumberSource(queue)` is the deterministic test double.

**D7 — `SavedSaleState` is the saved-sale contract shown before any print attempt.**

`SavedSaleState(ReceiptNumber, SaleId, Total, SavedPaymentLine[] Payments, PrintStatus PrintStatus · always Pending in BG-006, DateTimeOffset SavedUtc, bool Replayed)` — built by a pure projection `SavedSaleState.From(Sale, replayed)`, never by scattered VM formatting; a replayed submit shows the SAME receipt. Printing is BG-007: BG-006 never calls `IPrintTicketStore` from the App; the receipt-result stage renders the saved state with Print/Reprint actions inert and "Sale saved. Printer needs attention." copy. `PrintStatus { Pending, Printed, Failed }` (Core) is derived by `ISaleQueryService` from the audit stream (`PRINT`/`PRINT_FAILED` on a `SaleRef`, latest wins) — free for BG-007, no schema.

**D8 — Search: EF `IProductStore` extension + one NOCASE index; on-hand never touches Products.**

`IProductStore` gains `FindByBarcodeAsync` (exact TEXT equality — leading zeros significant by construction; the `IX_Products_Barcode` unique index exists) and `SearchByNameAsync` (case-insensitive substring, wildcard-escaped, `limit`; new `IX_Products_Name_NOCASE`). `IProductSearchService` composes SKU-exact (ordinal-ignore-case) → barcode-exact → name-substring, trims the scanner CR suffix at the boundary (hardware-profile: DS2208 CR default), and enriches each hit with live `OnHand` via `ITripReadService.OnHand`/hot fold — stock is never stored on products. P95 ≤ 100 ms @ 50,000 products is served by unique-index exact lookups + the NOCASE name index + a `LIMIT`-capped infix scan (single-digit ms at 50k rows), measured by the PerfRunner probe — the probe measures the real query, not a cache (Candidate B's warm-index fallback stays a documented fallback only, not the default).

**D9 — History: `ISaleQueryService` over the store's hot fold — no new store, no `ILedgerStore` extension, no EF mapping of raw sale tables.**

`Bind()` + `Context.Sales/Trips` + `State.Sale(id).Status` + `Movements`/`Audits` — exactly the `TripReadService`/`AdminReadService` pattern already in production. Sale status is fold-derived (never stored, never duplicated); `PrintStatus` derives from audits; results are byte-identical across restart and rebuild for free (lane 10). Peddler scoping with `TRIP_PEDDLER_MISMATCH` on cross-peddler access. No in-memory-mirror churn; no parity cost; no schema knowledge in two places (Candidate C's leak risk).

**D10 — What the design deliberately does not do.**

Prints nothing (BG-007). Persists no hold/draft (F4 is Future; the BG-006 unit list has no hold-persistence item). Adds no receipt-counter table (count-derived, D6). Adds no `ILedgerStore`/`InMemoryLedgerStore`/`CheckoutService`/`SaleBuilder`-semantics change beyond the two additive rules (D4, D5) and the additive `NewSaleLine.DiscountType` optional parameter. Adds no `SaleViews` projection/backfill (Candidate C's scale machinery — needless at BG-006 volumes; documented as the future path if history filtering ever needs SQL-side status indexing). Adds no FTS5 (infix scan is single-digit ms; FTS5 is a documented upgrade path). Adds no approval-record table (BG-007's approvals scope; the discount gate + sale document audit are the MVP record). Adds no discount-type schema column (D4).

### Invariants in types (branded vs invariant-typed)

- **Branded (existing):** `CheckoutKey` (the session's identity — a checkout key can never be passed where a `SaleId` is expected), `SaleId`, `SaleLineId`, `PaymentLineId`, `TripId`, `PeddlerId`, `ProductId`, `UserId`.
- **Invariant-typed:** `Money` (long centavos; `Discount`, `Amount`, `Tendered`, `Total`, `Change` ride it), `Quantity` (> 0; a line cannot express zero/negative), `DiscountType` (closed enum = the typed permitted set), `PrintStatus` (closed enum), `CheckoutSessionState { Building, Submitting, Saved }` (closed enum).
- **Plain strings by design:** `ReceiptNumber` and `Sku` (external labels; `R-000001..4` scenario receipts are legal committed values, so a branded receipt type would lie; the canonical format has exactly one producer — `CommittedSaleNumberSource` — on the interactive path, and global uniqueness is the DB invariant); `Reference` (free text per client policy; enforced by builder + dialog double-gate, D5 — a branded `PaymentReference` type was rejected: it ripples through `NewPaymentLine`/`PaymentLine`/serialization for no enforcement gain).
- Validation lives at two boundaries only: the session (actor, trip, product, price authority, discount, reference, change, receipt) and the store's append (fold guards + `VerifyCrossChecks` + constraints, unchanged). Inside, `SavedSaleState` and the query records are trusted.

### Interface depth

`CheckoutSession` (one object, ~9 members) hides: key discipline, actor derivation + `AUTH_ACTOR_*` guards, actor→peddler→trip resolution, product/price authority, discount policy + rounding, reference policy, payment normalization (split/change), receipt derivation + DUP retry, and the replay protocol. `ISaleQueryService` (4 methods) hides fold semantics, status derivation, print-status provenance, and peddler scoping. `IProductSearchService` (2 methods) hides search semantics (leading zeros, suffix trim, name infix) and the on-hand join. The ledger seam is untouched; nothing wire/schema-shaped appears on any public surface.

## Synthesis decision

**Base: Candidate A** (extend-the-seams). Decisive property: it touches zero pinned surfaces — no scenario source edit, no `SaleBuilder`/`ILedgerStore`/`InMemoryLedgerStore`/`CheckoutService` semantic change, no counter table, no parity cost; the whole interactive contract is one deep module over the existing single-transaction append; history reuses the proven `TripReadService` fold-read pattern (restart/rebuild-identical for free); search is real EF over a real index (the probe measures the query, not a cache). Cross-judge: A 51 / B 49 / C 41 — agreement on base.

**Grafted from B (checkout aggregate):**
1. **Session-typed exactly-once** — the `CheckoutKey` and the `Building/Submitting/Saved` state machine live in `CheckoutSession` (D1), upgrading base-A's VM-discipline key into a typed invariant while keeping the one-deep-module seam.
2. **Statutory 20/20 percentages + floor-truncation math** (D4) — fixes the base's wrong 5% senior rate.
3. **Live stock check at add/quantity time** (A7 upgrade): `AddProduct`/`AdjustQuantity` refuse `STOCK_INSUFFICIENT` at keystroke time, in addition to the builder's pre-append + in-txn re-check.

**Grafted from C (query-store split):**
4. **`IX_PaymentLines_Reference`** in the BG006_01 migration — lane-5 reference search scale-proof for future SQL exports.
5. **`BANK_REFERENCE_REQUIRED`** builder rule (D5) — scenario-safe (no bank lines), pure document validity.

**Rejected (with reason):**
- From B: the in-memory warm-search index as the default (the perf probe would measure a cache, not the product lookup; kept as a documented fallback); `E_REFERENCE_REQUIRED` for wallets in the builder (the only reason B needs a scenario source edit — wallets stay capture-and-persist at the dialog, D5); the SALE-audit discount-summary message change (touches the byte-stable seam for zero ledger value; the committed `SaleLine.Discount` money is the record).
- From C: the actor guard inside `ICheckoutService` (the whole A3 blast radius — force-edits `TripLifecycleScenario`/`LaneRunner`/`PerfRunner` construction sites and contradicts the byte-stable mandate); `SaleViews` + count-gated backfill (scale machinery — needless at BG-006 volumes; documented as the future path); the per-(trip,date) receipt counter (collides with the global UNIQUE index across peddlers — day-global won); capture-if-entered wallet references as the only story (bank is hard-required; wallets still captured); the "committed sale vanished" post-commit read on the saved-state path (saved state builds from the committed document, never a follow-up read that can fail).

**Red-flag screen (design-red-flags.md):** no candidate showed a shallow module (all hid real complexity behind small surfaces; the session is deep by construction and is not a pass-through — the policy it owns exists nowhere else); the only leakage risk — raw sale schema knowledge in two places (Candidate C's `SaleQueryDbContext` mapping vs migration SQL) — is neutralized by choosing the fold-read history (D9), which keeps all schema knowledge in one place (the store); no temporal decomposition (session/query/search own their knowledge); Candidate B's warm-index search service was borderline shallow and is demoted to a fallback.

**Convergence note:** all three candidates independently agreed the write path must stay untouched on the ledger connection, that exactly-once rides the existing store-level idempotency key, that receipt numbers must be overridable to keep the scenario byte-stable, and that tests land under `tests/BentaGo.Tests/Sales` (namespace `BentaGo.Tests.Sales`, `--filter Sales`). Where candidates converged, this synthesis ships the consensus; the divergences are resolved above.

## Tradeoffs accepted

- We accept **day-global count-derived receipt numbers with gaps** (a rolled-back append leaves a hole) in exchange for zero counter table, zero new transaction surface, and a uniqueness guarantee from the existing UNIQUE index — nobody re-reads a number, only stores and displays it.
- We accept **peek-then-commit receipt numbering** (count read before the append) in exchange for keeping every write inside the existing Append transaction; the race is closed by `BEGIN IMMEDIATE` single-writer serialization + the UNIQUE backstop + a bounded same-key retry. If a multi-terminal write path ever appears, this must be re-audited (documented risk).
- We accept **two `Bind()`s per checkout** (trip resolution, then number count — both before the single append) in exchange for staying entirely on the ledger store's synchronizing seam; `Bind()` is a document-set refresh, the same call `TripReadService` makes.
- We accept **discount type not persisted to schema** (money column only; type derivable from flags + policy, and see the audit trail) in exchange for zero migration; a `discount_type` column is a cheap later migration if BG-008 needs per-type reporting.
- We accept **wallet references captured-but-not-hard-required** (bank hard-required) in exchange for not inventing a client policy docs/03 leaves to the operator; flipping to required is a one-line builder gate.
- We accept **no role gate in the discount pure function** (product-flag-gated statutory discounts, per D4) in exchange for not blocking legally-required PWD/Senior discounts at the register; the literal 0001-5 "admin approves" reading and a discretionary-discount approval record are BG-007 approval scope — surface both in Open questions.
- We accept the **session + VM cart mirror** (two line lists) in exchange for keeping WPF `ObservableCollection`/INPC out of the Application layer; correctness rests on the single-threaded dispatcher + `Changed`-driven rebind, documented as an invariant.
- We accept a **full-table name infix scan** (index serves equality/prefix; infix is `LIMIT`-capped) in exchange for a plain indexed schema (no FTS5); the 100 ms rule is met by measurement, and FTS5 is the documented path if name search ever dominates.
- We accept **line price fixed at scan time** (0001-4 locked price) — an audited admin price change mid-cart does not reprice open lines; acceptable because price changes are rare and audited.

## Alternatives considered

1. **Candidate B — CheckoutSession aggregate as the whole sale** (session owns everything, zero migration, warm in-memory search index). Lost on: the in-memory index makes the perf probe measure a cache rather than the product lookup; the builder-level `E_REFERENCE_REQUIRED` for wallets forces the ONE scenario source edit this synthesis avoids; the actor guards cover only the UserId (peddler/trip caller-supplied) instead of deriving from the actor. Its genuine wins — typed key ownership, 20/20 truncation math, live stock checks — are grafted in; the aggregate SHAPE itself is absorbed as the base's deep module.
2. **Candidate C — read/write split with an EF-mapped SaleQueryStore + materialized `SaleViews` status projection.** Lost on: the actor guard inside `CheckoutService` (reopens the byte-stable construction sites), the per-(trip,date) counter (global-UNIQUE collision), and the heavier read machinery (EF mapping of nine raw tables + a sixth projection + count-gated backfill + six indexes) for an indexable page-8 status filter — history is not perf-gated at MVP volumes and the hot fold already answers it restart-identically. Its wins — the reference index and the bank-required rule — are grafted; `SaleViews` is documented as the future path if history filtering ever needs SQL-side status indexing.
3. **Thin-caller design (no session/orchestrator):** the VM or a small service calls `ICheckoutService` directly, holding the `CheckoutKey` and cart as today. Lost on interface depth: the multi-attempt protocol (replay, DUP retry, actor re-check, payment normalization, discount policy) would scatter across VM + dialog + glue; every App change would re-derive "keep the key stable" from prose. The session hides exactly what the thin design leaks.
4. **Extending `ILedgerStore` with sale reads** (list/detail queries on the mutation seam). Lost on: it drags the in-memory mirror into parity for every query member and couples the contract to fold internals — it hides nothing new and exposes more; the fold-read `ISaleQueryService` (D9) hides the store behind the same public surface it already has.
5. **Per-day `ReceiptCounters` table written inside Append.** Lost on: it adds a durable table + `ILedgerStore`/`InMemoryLedgerStore` parity + rebuild surface for a uniqueness property the existing UNIQUE index already guarantees at serialized-writer scale; the retry path is 3 lines. Judged "prefer NOT adding beyond the unit list".

## Open questions and risks

- **Discount percentage provenance (0001-5):** the PWD/Senior percentages live only in the CSV import preview, not the model; this design hard-codes statutory 20/20 at the boundary. Should BG-006 move override rates into AppSettings rows (the table exists), and does the operator want a literal admin-approval gate for ANY nonzero discount (0001-5 wording) rather than the product-flag gate (statutory practice)? The approval RECORD itself belongs to BG-007's approval surface — operator sign-off needed on what constitutes "approved" in the MVP.
- **Seller identity:** checkout binds to the actor's `PeddlerLink` + open trip — an Admin or Supervisor account with no peddler link cannot operate the register. Is that the intended operator model (a peddler-terminal per entity), or should trip selection be explicit?
- **Wallet references:** lanes 4-5 require bank; wallets "references persist". Require GCash/Maya references too (one-line builder gate), or keep capture-if-entered? Reconciliation (BG-008) may want them required — flagging now.
- **Midnight boundary:** receipt numbers and the history Day filter use the Manila day of the sale's `CreatedUtc`; a peddler selling past midnight spans two Manila dates — confirm "today" is the Manila trip day, not the UTC day (pinned in `ManilaDisplay`).
- **`--filter Sales` FQN collision:** the mandated command also matches existing `BentaGo.Tests.AppShell.SalesUx*` FQNs (xunit substring matching). Acceptable (they are sales-adjacent), or give the new suite a distinguishing marker?
- **Reopened previous-date trips:** a reopened trip's sales could collide with the day counter's count — the UNIQUE backstop + retry handles it; needs one integration test.
- **`CheckoutService` remains directly callable with an arbitrary `By`** (only the session path is guarded). Production's only checkout entry is the session, but an independent audit reviewer may want the guard also in the service WITH a documented context seam for harnesses — record as a follow-up if the review flags it.
- **Saved-sale `PrintStatus` after BG-007:** the flag is a UI-state promise here (no print call exists); BG-007's printer status must flow through the same result stage without re-asking to checkout.

## Next implementation step

Build `CheckoutSession` + `CheckoutSessionFactory` + `ActorGuard` + `CommittedSaleNumberSource` + `DiscountPolicy` in Core/Application against the existing seams (no schema yet), with `tests/BentaGo.Tests/Sales/CheckoutSessionTests` green on real SQLite via `PersistenceTestBase` (happy cash, actor guards, oversell + recoverable cart, duplicate Enter replay, `DUP_RECEIPT_NUMBER` retry, failed persistence) — the exactly-once core is fully testable before any migration or App wiring lands; then the search service + `BG006_01` indexes, then `ISaleQueryService`, then the App binding (separate interaction PR per the review gate).

## Docs/05 handoffs (verdicts per unit)

| docs/05 BG-006 checkbox | Verdict |
|---|---|
| Files: product search, scanner input, cart, discount, payment, checkout, receipt-result, transaction-history; tests under `tests/BentaGo.Tests/Sales/` | Served: `IProductSearchService` + `IProductStore` extension; existing scan-input path + CR suffix trim at boundary; cart via `CheckoutSession` lines (VM mirror); F7 + `DiscountPolicy`; session payment assembly + dialog reference fields; `CheckoutSession.SubmitAsync`; `SavedSaleState` + receipt-result stage; `ISaleQueryService` + `TransactionsViewModel` (un-parks `ScreenKeys.Transactions`, Alt+4); new `BentaGo.Tests.Sales` folder (namespace `BentaGo.Tests.Sales`). |
| Build: keyboard-HID scan handling | Served: F2/Enter scan → `ScanAsync` (barcode-exact then SKU-exact, leading zeros preserved); scanner focus restored after every completed scan (existing `FocusRequest`/`AutomationIds.SalesScanBox`; re-asserted). |
| Build: manual search | Served: F3 → `SearchAsync` (SKU → barcode → name; name NOCASE-indexed infix; results carry live OnHand). |
| Build: cart commands | Served: existing Delete / `+` / `-` bindings unchanged; session `AddProduct` dedupe + increment (lane 1: qty 2, one line); live `STOCK_INSUFFICIENT` cap. |
| Build: allowed discounts | Served: `DiscountType { None, Pwd, Senior }` + product-flag gates + statutory 20/20 floor math (F7); `DISCOUNT_TYPE_MISMATCH` builder invariant. |
| Build: Cash, GCash, Maya, Bank Transfer, Credit, split payments | Served: session assembly + untouched builder rules; dialog reference fields for wallets/bank; credit via `CREDIT_REFERENCE_REQUIRED`; `BANK_REFERENCE_REQUIRED`; split via zero-balance gate + `PAYMENT_TOTAL`. |
| Build: idempotent checkout | Served: session-owned stable `CheckoutKey` + store-level replay (`FindCheckout` fast path + in-txn key) + `Submitting` state gate + disabled `IsDefault` confirm. |
| Build: restore scanner focus; disable duplicate checkout while running; saved-sale state before any print attempt | Served: focus re-request to `Sales.ScanBox`; `CheckoutCommand` disabled while `Submitting`; `SavedSaleState` (PrintStatus.Pending) bound by the result stage; **BG-006 never prints** (BG-007 owns ESC/POS + retry/reprint). |
| Unit `--filter Sales`: product lookup | `ProductSearchTests` — SKU/barcode/name resolve the same product; leading-zero barcode; CR suffix trimmed; on-hand populated. |
| Unit: repeated scans | scan dedupe by `ProductId` increments quantity; focus restore asserted. |
| Unit: cart quantity | `Quantity` type rejects zero; `+`/`-` bounds; `AdjustQuantity` min 1. |
| Unit: stock limits | live add/quantity check + builder `STOCK_INSUFFICIENT` pre-append + in-txn fold guard; `OversellTests` — zero `Sales` rows, cart recoverable (lane 9). |
| Unit: discounts | `DiscountPolicyTests` — permitted-set exhaustiveness, flag gates, 20/20 centavos-exact truncation, `DISCOUNT_TYPE_MISMATCH`. |
| Unit: cash change | session `CashChange(amount, tendered)` pure math; builder `TENDER_INSUFFICIENT`/`CHANGE_MISMATCH` backstop; change only on the cash component. |
| Unit: every payment method | session assembly + builder receipts per non-Credit payment; `FundOf()` excludes Credit from funds (fold, unchanged). |
| Unit: split totals | dialog zero-balance gate + session sum==Total + builder `PAYMENT_TOTAL`. |
| Unit: credit requirements | `CREDIT_REFERENCE_REQUIRED` (existing) + dialog customer/reference gate; excluded from cash remittance by design. |
| Unit: idempotency / duplicate Enter | `ExactlyOnceTests` — same key twice → one sale, identical `SavedSaleState`, `Replayed: true` (lane 8); post-crash re-ask replays. |
| Unit: failed persistence | `FailNextAppendAfterInsert` / `DelayBeforeCommit` seams → `Result.Fail` → session back to `Building`, cart + payments intact, SAME key retries. |
| Live lanes 1–10 | L1 repeat-scan: qty 2 + focus; L2 search showing current stock: `SearchAsync`+OnHand; L3 cash exact: session change + fold funds; L4 wallets: references persist (dialog→`PaymentLine.Reference`), physical cash excludes via fund posting; L5 bank: `BANK_REFERENCE_REQUIRED` + `Query(Reference=…)`; L6 credit: fields persist, excluded from expected cash; L7 split: completion disabled until zero; L8 double-submit: one sale + one receipt (session key + replay); L9 oversell: refused before persistence, cart recoverable; L10 restart offline: history from the same file (nothing sale-related in memory), payment details + print status re-derived. |
| Perf: P95 lookup ≤ 100 ms and P95 checkout ≤ 2 s @ 50k products / 100k movements (1,000 lookups / 100 checkouts) | Lookups: unique barcode/SKU + NOCASE name index + capped infix (real query measured); checkout: unchanged single append + in-memory session assembly. Probe extended in `tools/BentaGo.Tools/PerfRunner` (1,000 `SearchAsync` + 100 `SubmitAsync`; baseline = BG-005 search + BG-003 transaction medians). |
| Review gate / merge | Live-lane PNGs + keyboard-only video per docs/05; stop at merge-ready; verdicts at head SHA; rebase; operator squash-merge. |
| Scenario/golden impact | **Zero scenario source edits.** `TwoPeddlersScenario` (receipts `R-000001..4`, fixed clock, `Seed "two-peddlers-v1"`) is untouched — no new movements/documents/audits; `PersistedGoldenTests` (in-memory run == SQLite run == reopen == rebuild) byte-stable; `LaneRunner.Lane2` pins sales==4 / lines==4 / payments==5 / movements==28 and tools GOLDEN_IDENT/RESTART/REBUILD checks unchanged. Goldens remain derived in-memory (no stored golden files). |

## Module map

```
src/BentaGo.Core/Domain/
  Builders/DiscountPolicy.cs                 [NEW] DiscountType, DiscountRates, DiscountPolicy.Allowed (pure, 0001-5)
  Documents/Documents.cs                     [EDIT, additive] + PrintStatus { Pending, Printed, Failed }
  Commands/Commands.cs                       [EDIT, additive] NewSaleLine += DiscountType (optional end param; existing callers compile)
  Builders/Builders.Sales.cs                 [EDIT, additive] + DISCOUNT_TYPE_MISMATCH, + BANK_REFERENCE_REQUIRED (scenario-safe rules)
src/BentaGo.Application/
  Contracts/Sales.cs                         [NEW] session contract + receipts + query contract + records (below)
  Contracts/IProductStore.cs                 [EDIT, additive] + FindByBarcodeAsync, + SearchByNameAsync
  Contracts/ILedgerStore.cs                  UNCHANGED
  Contracts/UseCases.cs, IActorContext.cs    UNCHANGED
  Services/CheckoutSession.cs                [NEW] the deep module (D1–D7)
  Services/CheckoutSessionFactory.cs         [NEW] actor → peddler → open trip; session construction
  Services/ActorGuard.cs                     [NEW] AUTH_ACTOR_* resolution helper (F8/BG-005d)
  Services/CommittedSaleNumberSource.cs      [NEW] ISaleNumberSource (day-global count)
  Services/SaleQueryService.cs               [NEW] ISaleQueryService (Bind()+Context/State/Audits)
  Services/ProductSearchService.cs           [NEW] IProductSearchService (sku→barcode→name + suffix trim + on-hand)
  Services/UseCaseServices.cs                UNCHANGED (CheckoutService byte-identical)
  Services/TwoPeddlersScenario.cs            UNCHANGED
src/BentaGo.Infrastructure/
  Persistence/ProductStore.cs                [EDIT, additive] barcode/name queries
  Migrations/<ts>_BG006_01_AddSalesIndexes.cs [NEW] 3 raw indexes, no tables, no data
  Hosting/BentaGoHost.cs                     [EDIT] register session factory, search, query, number source
src/BentaGo.App/                              [App chapter — prose + signatures only, NO XAML in this package body]
  Features/Sales/SalesViewModel.cs           session-driven; F7; saved-state stage (prose)
  Dialogs/Payment/PaymentDialogViewModel.cs  wallet/bank reference capture; bank-required gate (prose)
  Data/ServiceDataSource.cs                  [NEW] the BG-005 swap line: real reads behind IAppDataSource (prose)
  Features/Transactions/                     [NEW] un-parks ScreenKeys.Transactions; ISaleQueryService (prose)
tests/BentaGo.Tests/Sales/                   [NEW] namespace BentaGo.Tests.Sales (matches --filter Sales)
  SalesTestBase.cs, ProductSearchTests.cs, DiscountPolicyTests.cs, CheckoutSessionTests.cs,
  ExactlyOnceTests.cs, ReceiptNumberPolicyTests.cs, PaymentAssemblyTests.cs, ActorBindingTests.cs,
  OversellTests.cs, HistoryQueryTests.cs, SalesIntegrationTests.cs
```

## Signatures

### Core

```csharp
namespace BentaGo.Core.Domain.Builders;

/// <summary>The permitted discount set (0001-5): nothing else exists.
/// F7 cycles None -> Pwd -> Senior -> None; one type per line.</summary>
public enum DiscountType { None, Pwd, Senior }

/// <summary>Percentages live at the boundary, NOT in the model (0001: rates stay in the CSV
/// preview). Statutory default = PWD 20 (RA 10754), Senior 20 (RA 9994); overridable.</summary>
public sealed record DiscountRates(int PwdPercent, int SeniorPercent)
{
    public static DiscountRates Statutory { get; } = new(20, 20);
}

/// <summary>Pure, deterministic, integer-centavos discount math and policy.
/// amount = floor(unitPrice.Centavos * qty * percent / 100) — truncation toward zero;
/// never negative; exact for whole-peso statutory amounts.</summary>
public static class DiscountPolicy
{
    /// Allowed iff type == None (zero) or product.Supports{Pwd|Senior}Discount matches the type.
    /// Any other nonzero candidate → DISCOUNT_NOT_ALLOWED.
    public static Result<Money> Allowed(
        Product product, DiscountType type, DiscountRates rates,
        Money unitPrice, Quantity quantity);
}
```

```csharp
// Documents.cs — additive:
public enum PrintStatus { Pending, Printed, Failed }

// Commands.cs — additive (optional end param; existing call sites compile unchanged):
public sealed record NewSaleLine(
    SaleLineId LineId, ProductId ProductId, string Sku, string ProductName,
    Money UnitPrice, Quantity Quantity, Money Discount,
    DiscountType DiscountType = DiscountType.None);

// Builders.Sales.cs — two additive rules in the per-line / per-payment loops:
//   DISCOUNT_TYPE_MISMATCH  — (DiscountType == None) != (Discount.Centavos == 0); Field: line.Sku
//   BANK_REFERENCE_REQUIRED — BankTransfer with whitespace Reference; Field: "Bank"
// (both scenarios-safe: the scenario passes None+zero and has no bank lines)
```

### Application — contracts

```csharp
namespace BentaGo.Application.Contracts;

public enum CheckoutSessionState { Building, Submitting, Saved }

public sealed record SessionLine(
    SaleLineId LineId, ProductId ProductId, string Sku, string ProductName,
    Money UnitPrice, Quantity Quantity, Money Discount, DiscountType DiscountType);

public sealed record SavedPaymentLine(PaymentMethod Method, Money Amount, string? Reference);

/// <summary>The saved-sale contract the UI renders BEFORE any print attempt.
/// Printing is BG-007; PrintStatus is always Pending in BG-006.</summary>
public sealed record SavedSaleState(
    string ReceiptNumber, SaleId SaleId, Money Total,
    IReadOnlyList<SavedPaymentLine> Payments,
    PrintStatus PrintStatus, DateTimeOffset SavedUtc, bool Replayed)
{
    public static SavedSaleState From(Sale sale, bool replayed);   // pure projection; no follow-up reads
}

/// <summary>The deep module. One instance per sale; exactly-once is this object's ownership
/// contract: Key is minted once and reused on EVERY attempt; Submit no-ops while Submitting
/// and replays after Saved. New Sale = new session.</summary>
public interface ICheckoutSession
{
    CheckoutKey Key { get; }
    CheckoutSessionState State { get; }
    TripId Trip { get; }
    IReadOnlyList<SessionLine> Lines { get; }
    Money Total { get; }
    Money DiscountTotal { get; }
    string ReceiptNumber { get; }                 // reserved at first submit; re-minted only on DUP retry
    event EventHandler? Changed;                  // VM rebinds its ObservableCollection mirror

    Result AddProduct(Product product, Quantity quantity);   // repeated scan → same line, qty+1; live STOCK_INSUFFICIENT
    Result AdjustQuantity(SaleLineId lineId, int delta);     // min 1; live stock-limit re-check
    Result RemoveLine(SaleLineId lineId);
    Result ApplyDiscount(SaleLineId lineId, DiscountType type);   // permitted set + product flag + amount
    Result SetPayment(IReadOnlyList<PaymentLineDraft> drafts, Money change);  // assembly incl. split; validates total
    Task<Result<SavedSaleState>> SubmitAsync(CancellationToken ct = default);  // exactly-once; recoverable on failure
}

/// <summary>Session constructor: derives peddler/trip/location FROM the session actor
/// (F8/BG-005d) — AUTH_ACTOR_NOT_SIGNED_IN / AUTH_ACTOR_UNKNOWN / AUTH_ACTOR_DEACTIVATED /
/// NO_PEDDLER_LINK / NO_OPEN_TRIP — captures the actor, mints the CheckoutKey once.</summary>
public interface ICheckoutSessionFactory
{
    Task<Result<ICheckoutSession>> CreateSessionAsync(CancellationToken ct = default);
}

/// <summary>Receipt-number source. Overridable so tests/scenarios stay deterministic
/// (the scenario hard-codes R-000001..4 and bypasses the session entirely).
/// Format R-{yyyyMMdd}-{NNN} zero-padded, Manila day.</summary>
public interface ISaleNumberSource
{
    string Next(DateTimeOffset nowUtc, IReadOnlyCollection<string> alreadyAttempted);
}

public sealed record SaleHistoryFilter(
    DateOnly? Day, TripId? Trip, PaymentMethod? Method, SaleStatus? Status,
    string? Receipt, string? Reference);          // null = no filter; peddler always applies

public sealed record SaleHistoryRow(
    SaleId Id, string ReceiptNumber, DateTimeOffset CreatedUtc,
    PeddlerId Peddler, TripId Trip, LocationId Location,
    Money Total, SaleStatus Status,
    IReadOnlyList<SavedPaymentLine> Payments, PrintStatus PrintStatus);

public sealed record SaleLineDetail(
    SaleLineId Id, ProductId ProductId, string Sku, string ProductName,
    Money UnitPrice, Quantity Quantity, Money Discount, Money LineTotal);

public sealed record SaleDetail(
    SaleId Id, string ReceiptNumber, TripId Trip, PeddlerId Peddler, LocationId Location,
    DateTimeOffset CreatedUtc, UserId CreatedBy, Money Total, SaleStatus Status,
    IReadOnlyList<SaleLineDetail> Lines, IReadOnlyList<SavedPaymentLine> Payments,
    PrintStatus PrintStatus);                      // derived from PRINT/PRINT_FAILED audits

/// <summary>Read-only transaction history (lane 10, docs/03 page 8). Pure folds over
/// store.Bind() + Context/State/Audits — the TripReadService pattern; no ILedgerStore
/// extension, no in-memory-mirror churn, restart/rebuild identical for free.</summary>
public interface ISaleQueryService
{
    Result<IReadOnlyList<SaleHistoryRow>> Query(PeddlerId peddler, SaleHistoryFilter? filter = null);
    Result<SaleDetail> GetByReceiptNumber(PeddlerId peddler, string receiptNumber);
    Result<SaleDetail> GetByPaymentReference(PeddlerId peddler, string reference);  // lane 5
    Result<SaleDetail> GetById(PeddlerId peddler, SaleId saleId);   // cross-peddler → TRIP_PEDDLER_MISMATCH
}

public sealed record ProductHit(Product Product, Quantity OnHand);   // OnHand = movement fold, never stored

/// <summary>Offline product lookup (perf lane). Real EF query over indexed columns —
/// exact SKU (CI) → exact barcode (verbatim, leading zeros significant) → name infix
/// (NOCASE, wildcard-escaped, capped). CR scanner suffix trimmed at this boundary.</summary>
public interface IProductSearchService
{
    Task<Result<IReadOnlyList<ProductHit>>> SearchAsync(string query, int limit = 25, CancellationToken ct = default);
}
```

### Application — implementations

```csharp
// CheckoutSessionFactory.CreateSessionAsync:
//   User? user = ActorGuard.ResolveAsync(actors, users);      // AUTH_ACTOR_NOT_SIGNED_IN/UNKNOWN/DEACTIVATED
//   user.PeddlerLink is null                                  -> NO_PEDDLER_LINK
//   store.Bind(); open trip for user.PeddlerLink (one active) -> NO_OPEN_TRIP
//   return new CheckoutSession(store, products, checkout, numbers, user, actors, clock);

// CheckoutSession.SubmitAsync (exactly-once core):
//   1. State gate: Building only; Submitting/Saved no-op.
//   2. ActorGuard.Verify(actors, SessionUser)                 // AUTH_ACTOR_MISMATCH if context changed
//   3. per line: product = products.FindBySku(line.Sku)       // LINE_UNKNOWN_PRODUCT
//        UnitPrice = product.RetailPrice                      // 0001-4 locked; caller price ignored
//        discount = DiscountPolicy.Allowed(product, type, rates, price, qty)  // DISCOUNT_NOT_ALLOWED
//        on-hand cap re-check                                 // STOCK_INSUFFICIENT (builder re-checks in-txn)
//   4. per payment: bank reference required                    // REFERENCE_REQUIRED (builder backstop)
//        cash change = CashChange(amount, tendered)            // pure; TENDER_INSUFFICIENT backstop
//   5. receipt = numbers.Next(clock(), attempted)              // day-global count
//   6. cmd = new NewSaleCommand(new SaleId(Guid.NewGuid()), Trip, receipt, SessionUser.UserId,
//        clock(), lines(…, DiscountType), payments)            // By = session actor (F8)
//   7. outcome = checkout.Checkout(Key, cmd)
//   8. DUP_RECEIPT_NUMBER && retries < 1: receipt = numbers.Next(clock(), attempted ∪ {…}); goto 6 (SAME key)
//   9. failure: State = Building; return Fail(errors)          // recoverable cart (lane 9)
//  10. Saved: Replayed ? re-fetch committed sale by reference.SaleId (FindSale after Bind)
//        : new SavedSaleState(receipt, reference.SaleId, Total, payments, PrintStatus.Pending, cmd.NowUtc, false)
//        State = Saved; raise Changed; return Ok(saved)

// ActorGuard (mirrors UserAccountService, BG-005d):
//   ResolveAsync(IActorContext, IUserAccountStore) -> Result<User>   // NOT_SIGNED_IN/UNKNOWN/DEACTIVATED
//   Verify(IActorContext, UserId session)          -> Result         // NOT_SIGNED_IN / MISMATCH

// CommittedSaleNumberSource.Next: Bind once; day = "yyyyMMdd" of Manila(nowUtc);
//   count committed Sales with ReceiptNumber starting "R-<day>-"; return "R-<day>-{count+1:D3}";
//   skip alreadyAttempted. FixedNumberSource(queue) deterministic for tests.

// SaleQueryService: Bind; filter Context.Sales by peddler's trips + Day (Manila) / Trip / Method /
//   Reference (any PaymentLine, contains, OrdinalIgnoreCase) / Status (State.Sale(id).Status);
//   PrintStatus = latest PRINT/PRINT_FAILED audit on a SaleRef for the sale (none → Pending).

// ProductSearchService: trim; strip trailing '\r'; FindBySkuAsync (CI) → FindByBarcodeAsync →
//   SearchByNameAsync; per hit trips.OnHand(peddler, product.Id) → ProductHit (≤ limit hits).
```

### Infrastructure

```csharp
// ProductStore additions (EF):
Task<Product?> FindByBarcodeAsync(string barcode, CancellationToken ct = default);
//   SingleOrDefault(p => p.Barcode == barcode)                     // TEXT equality — leading zeros significant
Task<IReadOnlyList<Product>> SearchByNameAsync(string namePart, int limit, CancellationToken ct = default);
//   Where(EF.Functions.Like(p.Name, $"%{Escape(namePart)}%"))      // escape % and _; NOCASE index
//   .OrderBy(p => p.Name).ThenBy(p => p.Sku).Take(limit)

// Migration <ts>_BG006_01_AddSalesIndexes (Up; Down drops each):
migrationBuilder.Sql("CREATE INDEX IX_Products_Name_NOCASE    ON \"Products\"  (\"Name\" COLLATE NOCASE)");
migrationBuilder.Sql("CREATE INDEX IX_Sales_TripCreated       ON \"Sales\"     (\"trip_id\", \"created_utc\")");
migrationBuilder.Sql("CREATE INDEX IX_PaymentLines_Reference  ON \"PaymentLines\" (\"reference\")");
// raw-SQL indexes sit outside the EF snapshot — Down drops them explicitly; a schema test asserts
// their presence after migration.

// BentaGoHost: AddSingleton<ICheckoutSessionFactory, CheckoutSessionFactory>();
//              AddSingleton<ISaleNumberSource, CommittedSaleNumberSource>();
//              AddSingleton<IProductSearchService, ProductSearchService>();
//              AddSingleton<ISaleQueryService, SaleQueryService>();   // all alongside the existing host
```

### App layer (prose + signatures only — NO XAML in this package body)

- `ObservableObjectFactory`/`BentaGoHost`: register the four services above; keep `ICheckoutService`/`ILedgerStore` as-is; swap `IAppDataSource → ServiceDataSource` (the BG-004-planned one-line swap, finally executed): snapshot built from `IProductSearchService` (offers + on-hand), `ISaleQueryService` (dashboard/history), `ITripReadService` (stock rows); `Changed` fires after a committed checkout.
- `SalesViewModel`: `_session` created lazily via `ICheckoutSessionFactory.CreateSessionAsync` on first cart action; `Cart` mirrors `session.Lines` (rebind on `Changed`); scan/search call `IProductSearchService`; `CheckoutCommand` gate = `State == Building && Cart.Count > 0` (disabled while `Submitting` — lane 8); F12 → payment dialog → `session.SetPayment(result.Lines, result.Change)` → `SubmitAsync` → on Ok bind `ReceiptState(SavedSaleState, PrintAttempted: false)` ("Sale saved — Receipt {n}", payment summary, Print pending, New Sale / View Sale; Print/Reprint inert = BG-007); on Fail show corrective copy, cart + payments intact; F7 → `ApplyDiscount(selected.LineId, next type)`.
- `PaymentDialogViewModel`: gains a `Reference` capture field for Gcash/Maya/Bank (single-method and per-split-line); `CanConfirm` adds non-blank reference for `BankTransfer` and (existing) credit customer/reference; `Confirm()` populates `PaymentLineDraft.Reference` for e-methods.
- New `Features/Transactions/` (`TransactionsViewModel`): un-parks `ScreenKeys.Transactions` (Alt+4 Active in `ShortcutCatalog`), binds `ISaleQueryService` (filters day/trip/method/status/receipt/reference; detail incl. payments + print status); register via `ScreenRegistration` + `DataTemplate`.
- Esc/cancel + focus restoration unchanged (dialog service contract; `focusAfterClose: AutomationIds.SalesScanBox`).

### Tests (`tests/BentaGo.Tests/Sales/`, namespace `BentaGo.Tests.Sales`, `--filter Sales`)

- `SalesTestBase` — fresh temp root + `BentaGoHost.BuildForTests`, migrated; seeded catalog (incl. leading-zero barcode), seeded actor (peddler-linked user + admin) and open trip with stock → session tests run the REAL components.
- `ProductSearchTests` — SKU CI exact; barcode verbatim + leading zeros ("0123" ≠ "123"); name infix CI; CR suffix trimmed; unknown → none; OnHand populated.
- `DiscountPolicyTests` — permitted-set exhaustiveness; flag gates; 20/20 centavos-exact truncation (e.g. 4500 × 20 / 100 == 900); `DISCOUNT_TYPE_MISMATCH`.
- `CheckoutSessionTests` — happy cash exact change; wallet/bank `REFERENCE_REQUIRED`; credit; split via `PAYMENT_TOTAL`; actor guards (no session / mismatch / deactivated / no peddler link); `NO_OPEN_TRIP`; `LINE_UNKNOWN_PRODUCT`; price taken from the product; receipt format + day-monotonic count; `DUP_RECEIPT_NUMBER` retry-once (scripted source); failed persistence (`FailNextAppendAfterInsert`) → zero rows + key reusable.
- `ExactlyOnceTests` — duplicate Enter / retry-after-crash-injection: same key twice over both `InMemoryLedgerStore` and real SQLite → one sale, identical `SavedSaleState`, `Replayed: true`.
- `ReceiptNumberPolicyTests` — day padding/rollover, `FixedNumberSource` determinism, collision retry under the same key.
- `PaymentAssemblyTests` — cash change math; bank-reference builder backstop; split exact-tender cash.
- `ActorBindingTests` — `AUTH_ACTOR_*` via `ActorContextHarness` (explicit context seam; no login service).
- `OversellTests` — qty > fold on-hand refused (add-time AND submit-time), zero `Sales` rows, cart recoverable.
- `HistoryQueryTests` — filter matrix (day/trip/method/status/receipt/reference), peddler scoping (`TRIP_PEDDLER_MISMATCH`), voided-status derivation, `PrintStatus` Pending with no print audits, reopen/rebuild equivalence (lane 10 pattern).
- `SalesIntegrationTests` — representative seeded sales (cash / split / wallets / bank / credit + oversell + double-submit) through the full session → service → store path, reusing `PersistenceTestBase` helpers; plus a schema probe asserting the three BG006_01 indexes after migration.

**Scenario/golden impact (pinned):** `TwoPeddlersScenario` untouched — receipts `R-000001..4`, fixed clock, movement order, and rendered `ScenarioReport` byte-identical; `PersistedGoldenTests` and tools GOLDEN_IDENT/RESTART/REBUILD checks unchanged; `LaneRunner.Lane2` pins (sales==4, lines==4, payments==5, movements==28) unchanged. Goldens remain derived in-memory (fresh `InMemoryLedgerStore` run == SQLite run, after reopen, after rebuild) — no stored golden files, no re-generation step.