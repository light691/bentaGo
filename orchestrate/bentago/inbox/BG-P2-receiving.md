BG-P2: receiving + stock-in (with CSV import)
Status: ready-to-launch (launch after BG-P1 merges)
Author: coordinator, 2026-08-30

PREREQS
- BG-P1 merged on origin/main: day lifecycle (DayOpen/DayClose/DayReopen),
  one store location constant, Receiving + CashExpense movement kinds born,
  schema baseline regenerated, real SqliteAppDataSource swapped.
- Read git show origin/main:orchestrate/bentago/HANDOFF.md sections 2, 7, 8
  (locked decisions, standing rules, codebase facts) before starting.
- Read docs/01 (store-POS PRD) sections on receiving + import/export and
  docs/05 BG-P2 block: they are authoritative.

SCOPE
1. Receiving document + lifecycle: Draft -> Submitted (no edit after
   submit; corrections via an Adjustment with reason if wrong). Fields:
   supplier, date, reference, notes, encoder, lines (product, quantity,
   unit cost, optional expiry), per-line total, document total.
2. ReceivingBuilder + ReceiveCommand + service (application) + audit
   RECEIVING (actor, terminal, reason n/a, reference, import provenance
   when import-originated). One Receiving movement per line (kind
   Receiving), all lines atomic in one transaction. StockOnHand fold picks
   them up. Inventory value basis = unit cost (fold-derived).
3. Approval gate: submit requires Supervisor/Admin, or Cashier with
   approver (follow the corrections approver pattern). No approval needed
   for the Draft creation.
4. CSV import for receiving lines (NEW decision, operator-approved):
   - Same pattern as the product catalog import (ProductCsvMapper /
     ProductCsvProfile conventions): header-by-name (case-insensitive,
     order-tolerant), exact required columns (sku or barcode, quantity,
     unit_cost; optional product_name, expiry_date), unknown/missing
     columns = whole-file error with a clear message.
   - Preview + validation result + row errors + operator + timestamp +
     source filename + immutable import log (reuse the existing
     ImportPreview/ImportLog machinery).
   - Import creates a DRAFT receiving document with provenance recorded;
     the normal submit + approval gate still applies. A file never
     bypasses ledger rules.
   - No partial imports on failure; duplicates by sku/barcode held to the
     same rules as catalog import.
5. Stock screen: on-hand per product, low-stock threshold, receiving
   history, per-product last movement. Show receiving doc number, date,
   supplier, qty, cost.
6. Day gating: receiving does NOT require an open day (goods can arrive
   while the register is closed); but sales and corrections stay day-
   gated. Confirm the gate decision with the service layer.

TESTS
- Unit: builder legality (qty>0, cost>=0, sku/barcode resolves, expiry
  format), draft->submit transitions, submit atomicity + audit, approval
  gate (cashier without approver rejected), value basis fold, CSV
  validation incl. every row-error case + duplicate handling, no-partial-
  import, immutable log row, stock fold after receiving.
- Live lanes (env BENTAGO_LIVE_LANES=1, BENTAGO_HEAD): receiving-draft-
  submit (draft -> approve -> submit -> stock increases on the Stock
  screen), receiving-csv-import (import -> draft with row errors shown ->
  submit -> stock correct; PNG + verdict), receiving-cashier-no-approval
  (rejected, zero movements). Evidence verdicts + PNGs under
  artifacts/verification/bgp2/<BENTAGO_HEAD>/ and artifacts/review/.
- Perf: receiving submit on 10k-line CSV P95 <= 2s (rule; benchmark via a
  Bg002PerfRunner-style probe; write oracle trace/bentago/perf/bgp2/
  perf.tsv with head, seed, metric, verdict rows).

GATES
- dotnet build -c Release 0 errors 0 warnings; full dotnet test green;
  dotnet format --verify-no-changes 0 diagnostics; zero-literal XAML rule;
  usings alphabetical; git grep trip|peddl|load over src/tests/tools = 0
  live hits (receiving doc names must not reintroduce them).
- First-PR diff must NOT contain: customer/utang tables (P3), till/multi-
  till (P4), design overhaul (P5), docs edits, orchestrate edits.

WORKTREE
- git -C C:\BentaGo fetch origin main -q; git -C C:\BentaGo worktree add
  C:\BentaGo\.wt\bgp2 -b bgp2 origin/main. Commit locally, report SHA.
  No push/PR/orchestrate edits.

COMMIT
BG-P2: receiving (draft->submit, approval, CSV import, stock screen, lanes)
