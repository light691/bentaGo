# BentaGo design prompt v3 (store POS)

Successor to HANDOFF.md section 5's design prompt v2. Changes from v2: adds the features the program review surfaced (opening float, hold-and-recall, X-report, no-sale drawer, denomination cash count, payment reference capture), BIR-ready receipt content, and the patterns worth carrying from the two superseded peddling PNGs. Items marked [DECIDE] are parked in docs/backlog.md; the prompt asks the designer to show them as optional panels so the operator can pick with eyes open.

Paste everything below the line into the design model.

---

DESIGN TASK: Full design system + screen set for BentaGo, a standalone,
offline-first Windows POS for a single Philippine sari-sari store.

PRODUCT CONTEXT
- One Windows machine at the store counter. 1366x768 min, 1920x1080 target.
- One till, one cash drawer, one operator at a time. Roles: Cashier,
  Supervisor, Admin. Real approvals with reason: voids, damage, adjustments,
  post-close corrections, day reopen.
- USB barcode scanner + 80mm thermal printer (ESC/POS, 42 columns).
  Keyboard-first, every action tappable. F-keys matter; show them.
- Fully offline. SQLite on disk. No cloud, no sync, ever. The register opens
  in the morning with an opening cash float in the drawer, sells all day,
  closes the drawer at night. Day close compares expected cash to counted
  drawer: BALANCED / SHORTAGE / OVERAGE.
- Money is integer pesos/centavos. Totals large, right-aligned, no overflow.
  Products: sardines, instant coffee, charcoal, soap, canned goods.
  Payments: Cash, GCash, Maya, Bank Transfer, Credit/Utang, Split.
- The receipt is a BIR non-VAT Sales Invoice: sequential serial number,
  TIN, business name and address, "NON-VAT" legend, receipt is source of
  truth. Speed beats beauty. Errors are plain language ("Printer needs
  attention").

SCRIPTS: design, in this order.

1. REGISTER (the 90% screen, hero). Scan field (F3), search panel (SKU,
   barcode, name), cart with line editing, subtotal/discount/total large,
   change computed on the cash component. States: normal, scanner-active,
   printer-down ("Sale saved. Printer needs attention" + Reprint), hold.
   Features: HOLD SALE (F7) parks the cart with a recall chip strip;
   NO-SALE drawer-open button (audited); customer attach for utang;
   line discount shows locked state unless the role holds the grant.
2. PAYMENT DIALOG. Total, quick cash buttons (exact, 20/50/100/200/500/1000),
   amount tendered, change. Split payment: lines must equal the total, change
   only from the cash leg. GCash/Maya/Bank Transfer capture a REFERENCE
   NUMBER field with a "check the customer's confirmation" hint. Utang picks
   a named customer, shows current balance, warns near an optional credit
   limit [DECIDE].
3. DAY OPEN. Operator + role confirmed, opening float entry (denomination
   breakdown optional), date, till identity. Gate: a day must be open to sell.
4. DAY CLOSE (second most important). Expected cash vs counted cash with a
   denomination count entry (bills x count, coins x count) that rolls up,
   variance banner BALANCED / SHORTAGE / OVERAGE with the amount, notes,
   optional per-product physical count with variance value and reason,
   close statement print, reopen-with-approval flow (reason + approver,
   prior close stays visible). States: balanced, shortage, overage.
5. X-REPORT (mid-day). Read-only snapshot: sales so far, cash in drawer,
   payment breakdown, printable, no close, no lock. [DECIDE: include]
6. CORRECTIONS. VOID / RETURN / DAMAGE-LOSS / ADJUSTMENT. Reason required,
   approver picker (Cashier cannot self-approve), original receipt lookup
   for RETURN with a "never more than sold" guard. Confirmation shows the
   reversing movement in plain language.
7. TRANSACTIONS. Day/period list, totals, lookup by receipt number or scan,
   reprint with a REPRINT label, BIR serial number visible per row.
8. STOCK. On-hand list with low-stock and out states, cost and retail price,
   stock value, receiving history, manual ADJUSTMENT with reason. Optional
   column: expiry date [DECIDE].
9. RECEIVING (purchases). Supplier, lines with unit cost and quantity,
   draft -> submitted (submit raises on-hand), submitted is locked, reopen
   needs authorization, CSV import with preview and row errors. Barcode
   column, unit cost column.
10. CUSTOMERS (UTANG). List with balances, per-customer ledger (every credit
    and payment), pay-down entry with change computation, optional aging and
    write-off with approval [DECIDE].
11. REPORTS. Daily sales, payment breakdown, receivables, exceptions,
    inventory, reconciliation. Printable, not BI dashboards.
12. ADMIN. Users and roles (who may discount, who may approve), catalog
    import (header match, preview, row errors, immutable log), store profile
    (name, address, contact, TIN, NON-VAT legend -> receipt header), devices
    (printer test, scanner diagnostics, drawer test), backup now.
13. LOGIN / ROLE PICKER. Username, password, role, day-open state shown
    before entry.
14. FIRST-RUN WIZARD. Store profile, admin account, printer + scanner test,
    opening float default, finish -> day open. Runs once.
15. HELP OVERLAY. F-key reference, one keystroke.

RECEIPT MOCKUPS (true 80mm, 42 columns, monospace): sale invoice with
sequential serial number, date yyyy-mm-dd, business name + style + address +
TIN, "NON-VAT" legend, lines, totals, payment method + reference, cashier;
the same with REPRINT label; a close statement; a return credit slip.
Footer: "THIS DOCUMENT IS NOT VALID FOR CLAIM OF INPUT TAX".

DESIGN SYSTEM. Token set (color, type, 8px grid XS/S/M/L, radii, elevations,
states: primary/danger/success/balanced/warning/shortage/disabled/focused/
error) as a table + JSON. Two layout levels. Shell chrome: store name, date,
till identity, offline indicator, printer status, day-open/closed, signed-in
role. Large targets, high contrast, keyboard hints on every actionable
control.

DELIVERABLES. Three distinct directions (A. dense dark register console for
a veteran cashier; B. clean light kiosk for touch-first; C. paper/ink neutral
for a budget store). One hero Register each at 1366x768 with a one-paragraph
rationale. Recommend one. Then the full screen set at both resolutions, the
token table + JSON, the receipt mockups, and a one-page state matrix.
No web-dashboard look, no e-commerce mockup, no "sync later" language, no
cloud imagery.
