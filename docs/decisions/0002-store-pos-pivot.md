# 0002 — Store-POS pivot

- Status: ACCEPTED (operator decision, 2026-08-30)
- Deciders: operator; recorded by the BG-001..BG-008 coordinator session
- Supersedes (in part): [0001-mvp-operating-policy.md](0001-mvp-operating-policy.md)
- Program state at decision: BG-001..BG-008 merged (main `1852df1`); record also in `orchestrate/bentago/ledger.tsv` (PIVOT + HANDOFF row) and `orchestrate/bentago/HANDOFF.md` sections 1-2

## Context

The first program built BentaGo as a peddling/route-selling tool: peddlers, trips, stock loads, per-trip reconciliation. The first eight units delivered that product's foundation, shell, sales, hardware, and close flows. Before the release unit ran, the operator re-examined the product against how the store actually operates and concluded the peddling premise was wrong for the business.

## Decision

BentaGo becomes the store's main POS. No peddling: trips, peddlers, loads, route selling, per-peddler locations, per-trip reconciliation, and stock-load CSV exchange must not survive as live concepts in docs or code. The store-POS MVP is:

- One store, one till, one warehouse, one operator at a time; fully offline on local SQLite.
- The day (shift) is the accounting unit: open register in the morning, sell all day, close the drawer at night. Day close compares expected cash (cash sales minus cash refunds minus approved cash expenses) to counted cash, labeled BALANCED / SHORTAGE / OVERAGE; reopen requires approval and keeps the prior close.
- Stock enters via receiving/purchase documents (supplier, lines, unit cost, date, reference; draft vs submitted).
- Customer credit (utang) stays, upgraded from record-only to a full receivables ledger: named customers, balances, pay-down, ledger. Excluded from expected cash.
- Corrections: VOID / RETURN / DAMAGE-LOSS / ADJUSTMENT, reason + approver, audited.
- Receipts: 80mm ESC/POS, 42 columns; the receipt is the source of truth. Roles: Cashier / Supervisor / Admin. Store profile + first-run wizard in the MVP. Design overhaul (Register-first shell) is in scope.
- Multi-till is future only: till identity is laid in during BG-P4 so it becomes possible without a rewrite, and at most one line per spec may say "multi-till ready later".

## Execution sequence

BG-P0 rewrite docs/01 + docs/05 to the store-POS domain; BG-P1 domain pivot (delete the trip axis, day lifecycle, schema reset, real SQLite data source); BG-P2 receiving; BG-P3 customer credit; BG-P4 tills; BG-P5 design overhaul + first-run wizard + store profile; BG-009 packaging/recover/pilot. The program is done when the store-POS MVP passes the pilot and every earlier PR is merged.

## Consequences

- docs/00-04 keep their process and engineering law but their domain content is historical; docs/01 and docs/05 are the current authority.
- The first program's code remains on main until BG-P1 deletes or converts the trip/peddler axis (inventory in `orchestrate/bentago/HANDOFF.md` section 8).
- Model routing for UI generation defaults to `deepseek-v4-flash-vision-exp`; `glm-5.3-flash` only when warranted; verification always deepseek. (Operator-approved, 2026-08-30. Delegation-route pinning changed 2026-08-30: delegated children run `glm-5.3-flash` at high effort — see HANDOFF.md section 3b.)
- V2 ideas go to a backlog note, never into MVP scope (rule from docs/00, still binding).
