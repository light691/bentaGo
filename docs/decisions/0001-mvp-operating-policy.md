# 0001. MVP operating policy (Gate 0)

**Status note, 2026-08-30.** SUPERSEDED-IN-PART by `docs/decisions/0002-store-pos-pivot.md`, the store-POS pivot. Rows 4, 5, 7, 8, and 10 speak of peddlers, remittances, returns, or closed trips. They describe the abandoned peddling product and no longer apply. Row 6 is overturned by the pivot. Its answer, "Record-only. Sale and customer reference recorded. Excluded from expected cash. No receivables ledger beyond reporting", is reversed: BG-P3 in `docs/05-pstack-program-execution-plan.md` delivers a full receivables ledger with named customers, outstanding balances, pay-down, and a ledger of every credit and payment. The consequence line "A receivable reference is recorded on the sale" falls with row 6. Every decision row below is kept exactly as accepted.

**Status:** Accepted
**Date:** 2026-08-29
**Scope:** docs/00-pstack-style-master-plan.md section 3, Gate 0. Bind schema, calculations, permissions, and compliance surface. Supersedes the draft record `docs/05-gate-0-decisions.md` (removed).

| # | Decision | Answer |
|---|---|---|
| 1 | Sales tax / receipt policy | Both, configurable. One template set switchable by setting: internal sale slip and formal tax receipt. Receipt header fields (business name, TIN, address) configurable. |
| 2 | Product identity | SKU authoritative; barcode optional, unique when present. Products may have no barcode (manual sale). |
| 3 | CSV source | Versioned UTF-8 templates now. Client real exports remain a release gate. |
| 4 | Price ownership | Imported price locked. Admin-only override with audit event. Peddler cannot change price. |
| 5 | Discount policy | Disabled unless an admin approves a per-sale discount. No peddler self-discount. |
| 6 | Utang / credit | Record-only. Sale and customer reference recorded. Excluded from expected cash. No receivables ledger beyond reporting. |
| 7 | Cash expenses | Not in MVP. Remittance equals gross. |
| 8 | Count cadence | Configurable. Cash count daily or weekly (default daily). Stock count daily or weekly (default weekly, at return). |
| 9 | Returns | Admin-approved. Return references the original sale. Never exceeds sold quantity. |
| 10 | Void authority | Admin PIN plus reason. No peddler self-void. Closed trips need controlled reopen. |
| 11 | Hardware | Plan defaults. 1366 by 768 and 1920 by 1080 test targets. 80 mm ESC/POS profile. Scanner as HID text plus Enter. Specific models unknown until hardware profile update. |

**Consequences:**

- Setting `ReceiptMode` with values `InternalSlip` and `FormalReceipt`.
- Settings `CashCountCadence` and `StockCountCadence` with values `Daily` and `Weekly`. Defaults Daily and Weekly.
- `SaleLine` discount requires an admin approval record. No discount otherwise.
- `SaleLine` price snapshots from the locked price; only admin override writes a new effective price with audit.
- Credit sales excluded from expected cash. A receivable reference is recorded on the sale.
- Void requires verified admin PIN and reason. Reopen is an audited controlled action.

**Overridable:** any row by a follow-up decision record. Revisions of rows 1, 5, 6, or 10 impact schema and need a migration note.
