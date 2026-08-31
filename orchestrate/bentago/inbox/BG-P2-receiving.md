BG-P2: receiving + stock-in (with CSV import)
Status: ready-to-launch (launch after BG-P1 merges). Corrected 2026-08-30: paths, movement kinds (ADR 0004), perf rule aligned to docs/05, submitted-receiving reopen restored, lane set restored to the plan's seven + the operator-approved CSV lane, evidence paths fixed.
Author: coordinator

PREREQS
- BG-P1 merged on origin/main: day lifecycle (DayOpen/DayClose/DayReopen),
  one store location constant, movement kinds GoodsReceived / FundIn /
  FundOut (ADR 0004 — there are NO kinds named Receiving or CashExpense),
  schema baseline regenerated with the BIR serial-range and terminal-identity
  fields (ADR 0003), real SqliteAppDataSource swapped in.
- Read orchestrate/bentago/preferences.md (standing orders — verbatim law),
  docs/decisions/0004-expected-cash-formula-correction.md, then docs/01
  (receiving + import/export sections) and docs/05 BG-P2 block: they are
  authoritative.

SCOPE
1. Receiving document + lifecycle: Draft -> Submitted. A submitted receiving
   is LOCKED; reopening it requires authorization (docs/01: a submitted
   receiving is locked, reopening requires authorization). Reopening
   reverses the stock movements as reversing entries and marks the document
   REOPENED; it is not an edit-in-place. Stock-level mistakes on a locked
   document can also be fixed with an Adjustment (reason + approver), but
   wrong supplier/reference/date is what authorized reopen is for.
   Fields: supplier, date, reference, notes, encoder, lines (product,
   quantity, unit cost, optional expiry), per-line total, document total.
2. ReceivingBuilder + ReceiveCommand + service (application) + audit
   RECEIVING (actor, terminal, reference, import provenance when
   import-originated). One GoodsReceived movement per line, all lines atomic
   in one transaction. StockOnHand fold picks them up. Inventory value basis
   = unit cost (fold-derived).
3. Approval gate: submit requires Supervisor/Admin, or Cashier with approver
   (follow the corrections approver pattern). No approval needed for Draft
   creation. Authorized reopen requires Admin (or Supervisor + reason) and
   is audited.
4. CSV import for receiving lines (operator-approved decision, recorded in
   ledger 2026-08-30):
   - Same pattern as the product catalog import: header-by-name
     (case-insensitive, order-tolerant), exact required columns (sku or
     barcode, quantity, unit_cost; optional product_name, expiry_date),
     unknown/missing columns = whole-file error with a clear message.
   - Preview + validation result + row errors + operator + timestamp +
     source filename + immutable import log (reuse the existing
     ImportPreview/ImportLog machinery).
   - Import creates a DRAFT receiving with provenance recorded; the normal
     submit + approval gate still applies. A file never bypasses ledger
     rules. No partial imports; duplicates held to catalog-import rules.
5. Stock screen: on-hand per product, low-stock threshold, receiving
   history, per-product last movement. Show receiving doc number, date,
   supplier, qty, cost.
6. Day gating: receiving does NOT require an open day (goods can arrive
   while the register is closed); sales and corrections stay day-gated.

TESTS
- Unit: builder legality (qty>0, cost>=0, sku/barcode resolves, expiry
  format), draft->submit transitions, submit atomicity + audit, approval
  gate (cashier without approver rejected), authorized reopen (movements
  reversed, document marked REOPENED, audit), value basis fold, CSV
  validation incl. every row-error case + duplicate handling,
  no-partial-import, immutable log row, stock fold after receiving.
- Live lanes (env BENTAGO_LIVE_LANES=1, BENTAGO_HEAD), the plan's seven
  plus the operator's CSV lane, evidence under
  artifacts/verification/bg-p2/<BENTAGO_HEAD>/ and review media to
  artifacts/review/:
  1 receiving-draft-submit (stock increases on the Stock screen)
  2 receiving-submit-state (submitted is locked; adjustment path shown)
  3 receiving-reopen (authorized reopen reverses movements, audited)
  4 receiving-validate (row errors, no partial import)
  5 receiving-low-stock (on-hand threshold flips the stock state)
  6 receiving-restart-persistence (kill mid-draft; draft survives restart)
  7 receiving-warehouse (one store location constant everywhere)
  8 receiving-csv-import (import -> draft with row errors -> submit ->
    stock correct; PNG + verdict)
  9 receiving-cashier-no-approval (rejected, zero movements)
- Perf (aligned to docs/05 BG-P2): time to commit a receiving with 1,000
  lines; FAIL when P95 exceeds 1 second. Benchmark via a Bg002PerfRunner-
  style probe; write orchestrate/bentago/perf/bg-p2/perf.tsv with head,
  seed, metric, verdict rows.

GATES
- dotnet build -c Release 0 errors 0 warnings; full dotnet test green;
  dotnet format --verify-no-changes 0 diagnostics; zero-literal XAML rule;
  usings alphabetical.
- Grep gate (live concepts only, per docs/05): `git grep -inE "peddl|\btrips?\b" -- src tests tools` = 0 hits, and `git grep -inE "\b(peddler|peddling)\b" -- src tests tools` = 0. The English word "load" (payload/loading) is NOT banned; `Trip`, `Peddler`, `Peddling` as identifiers are.
- First-PR diff must NOT contain: customer/utang tables (P3), till (P4),
  design overhaul (P5), docs edits, orchestrate edits.

WORKFLOW
- git -C D:\bentago fetch origin main -q; git -C D:\bentago worktree add
  D:\bentago\.wt\bgp2 -b bgp2 origin/main.
- Push branch bgp2 to origin when gates are green. The coordinator opens
  the PR and squash-merges per standing order 13. Report the head SHA.

COMMIT
BG-P2: receiving (draft->submit, authorized reopen, approval, CSV import, stock screen, lanes)
