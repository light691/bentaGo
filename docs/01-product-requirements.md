# BentaGo Product Requirements Document

**Status:** MVP handoff baseline  
**Product:** BentaGo  
**Tagline:** Offline-first peddling POS for route-based grocery selling  
**Platform:** Windows laptop POS  
**Primary hardware:** USB barcode scanner and thermal receipt printer  
**Integration:** CSV import/export with the existing grocery POS

## 1. Product definition

In this project, “peddling” means route or van sales: a salesperson takes selected inventory out of the grocery store, sells directly to customers while travelling, accepts cash or other payment, and later returns unsold stock and remits money.

BentaGo is a separate operational system for that movement. It must preserve enough evidence for the store owner to answer: what was loaded, who received it, what was sold, what was returned or damaged, what remains, and how much money was remitted.

## 2. Problem and objective

The client already has a grocery POS for in-store sales, but that workflow does not represent stock held by multiple travelling sellers. BentaGo must make route selling fast while keeping each peddler’s inventory and cash accountable.

Core objective: allow a peddler to receive allocated stock, sell offline, print a receipt, and close a trip; allow an admin to reconcile the resulting cash and inventory and exchange reliable files with the main POS.

## 3. Users and roles

### Peddler

Starts a trip, views assigned stock, sells, accepts payment, prints receipts, records returns/damage/loss, and performs their own close. They see only their permitted operational data.

### Store Admin / Owner

Manages products, peddlers, users, stock loads, imports, overrides, returns to store, adjustments, reports, backups, printer settings, and final reconciliation.

### Supervisor / Encoder

Loads stock, assists with imports and reports, and reviews transactions. Permissions may exclude user management, configuration, and high-risk adjustments.

Users and peddlers are separate concepts. A person can change role or stop selling while historical transactions remain intact.

## 4. Operating model

Each peddler is an inventory location. Main Store → Peddler Juan and Main Store → Peddler Pedro are distinct balances. A peddler cannot sell another peddler’s stock.

### Trip lifecycle

`Draft → Loading → Active → Reconciling → Closed`  
Cancellation is permitted only before sales, with an authorized reason.

1. Admin creates a trip for a peddler.
2. Main store issues an initial or additional stock load.
3. Peddler starts the trip and sells from that location.
4. Sales reduce peddler stock and create inventory movements.
5. Peddler records returns, damage, loss, and any approved expenses or adjustments.
6. Unsold goods may be returned to the main store.
7. Peddler counts/remits payment and submits the trip close.
8. Admin reviews variances and closes or reopens with a reason.

## 5. Reconciliation model

Cash is reconciled daily; full physical inventory counting is daily only if the client wants strict control, otherwise at return or weekly.

`Expected cash = cash sales - cash refunds - approved cash expenses/adjustments`.

Non-cash sales remain revenue but are excluded from expected physical cash. Credit/utang is recorded as receivable and excluded from cash remittance.

For each product:

`Expected remaining = starting stock + loads - sold - damaged - lost - returned to store + returns to stock ± adjustments`.

The system compares expected cash/stock with actual remitted/count values and labels the result balanced, shortage, or overage. Variances are never silently corrected.

## 6. MVP functional scope

### Authentication and session

- Local username/password login with role permissions.
- Start/end shift or trip and one active trip per peddler unless an admin overrides.
- Lock closed trips against casual editing.
- Record actor, time, terminal, and reason for sensitive actions.

### Catalog

- SKU, barcode, name, category, unit, cost, selling price, active flag, created/updated timestamps.
- Barcodes are unique; manual-sale products may have no barcode.
- Inactive products remain available in historical reports but cannot be newly sold.

### Stock loading

- Create a load document with load number, peddler, date, source, lines, encoder, and notes.
- Support initial and additional loads on the same day.
- Validate availability in Main Store before approving a load.
- Increase peddler inventory and record a movement for every line.

### Sales

- Barcode scan adds a product; scanning again increments quantity.
- Manual search by SKU, barcode, or name.
- Cart quantity changes, line discount if permitted, subtotal, total, and optional customer.
- Payment types: Cash, GCash, Maya, Bank Transfer, Credit/Utang, and Split Payment.
- Cash payment calculates change.
- Checkout saves sale, payment lines, inventory movement, and audit event atomically.
- Receipt prints after a successful save; a printer failure must not duplicate the sale.

### Returns, voids, and adjustments

- Void requires a reason and may require an admin PIN.
- Return references the original sale and prevents returning more than sold.
- Valid return-to-stock restores peddler inventory; return-to-store transfers it to Main Store.
- Damage, spoilage, loss, and corrections require a reason and permission.

### Closing and reports

- Daily sales summary, payment breakdown, expected cash, actual remittance, difference, notes, and close status.
- Optional physical count by product with variance.
- Reports: sales, products, payments, peddlers, inventory movements, load/sold/remaining, reconciliation, and exceptions.
- CSV export plus printable closing summary.

### Import/export

Import product catalog and stock-load CSV files. Export transaction detail, summarized inventory movements, reconciliation, and closing reports. Every import has a preview, validation result, row errors, operator, timestamp, source filename, and immutable import log.

## 7. Business rules

- Inventory must never change without a corresponding movement record.
- A sale cannot have zero lines, inactive products, or insufficient stock unless an explicit role-based override exists.
- Sale numbers and load numbers are unique and human-readable; internal IDs are UUIDs.
- Split payment totals must equal sale total.
- Cash change applies only to the cash component.
- A sale is committed once; retrying a print must not create another sale.
- Voids and returns create reversing movements; original records remain visible.
- Manual adjustments require a reason; large adjustments require approval.
- Closed trips are immutable except through a controlled reopen workflow.

## 8. Non-functional requirements

- Fully usable without internet.
- Product lookup feels instant; normal checkout completes in under two seconds on target hardware.
- App starts within five seconds on a reasonable Windows laptop.
- SQLite writes are transaction-safe and resilient to normal power loss.
- Automatic daily backup, with configurable retention and a restore check.
- Keyboard-first interaction with large touch-friendly fallback controls.
- Passwords hashed locally; sensitive actions auditable.
- Clear import/export and hardware error messages suitable for non-technical staff.

## 9. Edge cases and mitigations

Duplicate barcodes: reject before import.  
Scanner not focused: keep a dedicated scan input and visible focus state.  
Printer disconnected: save first, then offer retry/reprint.  
Double-click checkout: disable button while committing and use an idempotency key.  
Power loss during checkout: use one database transaction and recover on restart.  
Peddler forgets to close: show open-trip warning and let admin close/reconcile.  
Negative inventory from bad import: reject load or require authorized override.  
Missing products: keep rejected rows in an error report; do not guess mappings.

## 10. MVP acceptance criteria

The MVP is ready for pilot when a peddler can sell using a scanner and printer while offline; stock, sale, payment, and ledger records remain consistent; receipts can be retried without duplicate sales; daily cash close works; returns and variances are visible; CSV reports can be reconciled to the main store; backups restore successfully; and an admin can explain every inventory difference from the movement history.

## 11. Deferred enhancements

Cloud sync, central dashboard, multiple-device synchronization, customer ledger, customer-specific pricing, route planning/GPS, commissions, expenses, promotions, digital receipts, Bluetooth/mobile version, payment-provider APIs, and direct integration with the main POS.

