# 0004 — Expected-cash formula correction and post-close correction semantics

- Status: ACCEPTED (coordinator-ratified under the operator's 2026-08-30 standing order to proceed with the trajectory; date this session)
- Deciders: operator (delegated), coordinator on three-reviewer evidence
- Amends: docs/01 section 4 (Expected cash), HANDOFF.md section 2 (day close bullet)
- Related: docs/decisions/0002-store-pos-pivot.md, the program review (swarm, this session)

## Context

Three independent reviewers of the plan converged on the same defect. `docs/01` section 4 states `expected cash = cash sales - cash refunds - approved cash expenses`. That formula omits two real cash flows and leaves a third without a feature:

1. **Opening float.** The drawer starts the day with petty cash. A balanced day counts float plus takings, which exceeds the stated formula by exactly the float, so every ordinary day reads as OVERAGE.
2. **Cash pay-downs (utang collections).** A pay-down is a cash receipt into the drawer. It is revenue against a receivable, not a sale, and has no term in the formula.
3. **Approved cash expenses** appear in the formula, but no feature, screen, or movement records a pay-out. The lost P1a checkpoint had already ratified a `FundOut` movement kind for exactly this.

## Decision

1. The expected-cash formula becomes:

```text
Expected cash = opening float + cash sales - cash refunds - approved cash expenses + cash pay-downs received
```

2. Day open records the opening float (denomination breakdown optional). Day close counts the drawer including the float; the close statement shows expected, counted, difference, label.
3. Cash pay-downs received add to expected cash on the day they are received. A pay-down is a ledger event on the customer's receivable AND a `FundIn` movement on the cash fold. The two sides are one atomic transaction.
4. Approved cash expenses are `FundOut` movements with a reason and, where the grant requires, an approver. They are first-class documents, not a day-close text field.
5. Movement-kind names are FIXED as `GoodsReceived`, `FundIn`, `FundOut` (plus the existing sale/refund/correction kinds). Receiving documents create `GoodsReceived` movements. This resolves the P1-to-P2 naming seam (the BG-P2 brief said `Receiving`/`CashExpense`; those names die).
6. Post-close correction semantics: a closed day is immutable. Corrections always attach to the currently open day. A mistake discovered after close is corrected ON the open day with a reference to the original receipt; the closed day's recorded variance stands as history. Amending a closed day itself requires the reopen flow with approval.
7. docs/01 section 4 and the BG-P1 build box take this formula in the next docs pass; every worker brief for BG-P1 and BG-P3 inlines it verbatim.

## Consequences

- BG-P1 Core (in restart) implements the day lifecycle with the float, and the fold carries `FundIn`/`FundOut`.
- The reconciliation report changes: expected cash derivation includes float and pay-downs. BG-008's already-merged reconciliation tests will be retargeted in P1e with golden fixtures updated deliberately, not silently.
- The BIR serial-range and terminal-identity fields from ADR 0003 stay in the BG-P1 schema reset.
