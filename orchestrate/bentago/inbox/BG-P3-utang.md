BG-P3: customer credit (utang) — receivable ledger, pay-down, screens
Status: queued (launch after BG-P2 merges)
Author: coordinator, 2026-08-30

PREREQS
- BG-P2 merged. Read orchestrate/bentago/preferences.md, docs/01 utang
  section, docs/05 BG-P3 block, ADR 0004 (FundIn + expected cash).

SCOPE
1. Customer domain: named customers (name, phone optional, notes, active),
   table + migration appended AFTER the baseline (sequential migrations,
   backup before apply, standing order 11).
2. Credit sale: a sale with an Utang payment line creates a receivable for
   the attached customer; excluded from expected cash (fold rule already in
   Core from P1a).
3. Pay-down: a pay-down document (customer, amount, method Cash or
   non-cash, reason optional). Cash pay-down = FundIn movement + receivable
   ledger credit, ONE atomic transaction (ADR 0004 decision 3). Non-cash
   pay-down credits the receivable without touching expected cash.
   Over-payment rejected with a clear error (docs/05 rule stands).
4. Receivable ledger: every credit and payment appends; balances derived by
   fold; original records never edited; nothing silently corrected.
5. Screens: Customers list with balances; per-customer ledger view;
   pay-down entry with change computation for cash.
6. Reports: receivables CSV export (RFC-4180, invariant, Asia/Manila)
   joined to the existing export set; payment-breakdown report includes
   pay-downs.
7. OUT OF SCOPE (parked in docs/backlog.md, do not build): credit limits,
   aging, write-off.

TESTS
- Unit: receivable creation on utang sale, pay-down atomicity (FundIn +
  ledger row), over-payment rejection, non-cash pay-down (no expected-cash
  effect), balance derivation vs ledger fold, inactive customer cannot take
  new credit but history remains.
- Lanes: utang-sale (credit sale -> receivable on report), pay-down-cash
  (drawer increases, balance drops, one ledger row), pay-down-noncash,
  paydown-over-rejected. Evidence to artifacts/verification/bg-p3/<head>/.

GATES
- Build 0/0, full suite green, format clean, grep gate per standing rules.
- Perf: receivables report at the docs/05 budget (50,000 customers is the
  plan's number; coordinator treats a miss as recorded issue, not an
  automatic block, per the P1e note).

WORKFLOW
- Worktree D:\bentago\.wt\bgp3, branch bgp3 from origin/main. Push when
  green; coordinator squash-merges (standing order 13).
COMMIT
BG-P3: utang (customers, credit sales, pay-downs with FundIn, ledger, screens, export)
