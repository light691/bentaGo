# BentaGo product requirements

**Status:** store-POS reference
**Product:** BentaGo
**Definition:** the main point of sale for one Philippine store
**Platform:** Windows desktop, WPF
**Primary hardware:** USB barcode scanner and 80mm thermal receipt printer, ESC/POS, 42 columns
**Storage:** local SQLite, offline first
**Currency:** integer Philippine peso centavos
**Timezone:** Asia/Manila

## 1. Product definition

BentaGo is the main point of sale for one store. One Windows machine sits at the store counter. One operator works the till at a time. The system runs fully offline. No cloud service exists. A sale never needs a network. Data lives in a local SQLite database on the machine.

The register opens in the morning, sells all day, and closes the drawer at night. The day, not a route, is the accounting unit. Stock enters the store through purchases and receiving. A customer may buy on credit, recorded as utang. Corrections and the day close are audited.

The receipt is the source of truth for a sale. The receipt header comes from the store profile: name, address, contact, and TIN.

The store has one warehouse and one till. Multi-till is a future idea only. At most one line in this document and in the program plan mentions that multi-till may come later.

## 2. Users and roles

Three roles apply to the whole system. Every sensitive action records the actor, the terminal, the time, and the reason.

### Cashier

Operates the register. Opens the day, sells, accepts payment, prints receipts, and performs the day close within the allowed grant. Sees counter data only.

### Supervisor

Works within the cashier grant and adds overrides. Approves corrections that a cashier cannot approve, approves a reopen, and reviews reports. Cannot manage users or store configuration.

### Admin

Owns everything. Manages users and roles, the catalog and catalog import, the store profile, devices, backup and restore, and the final review of accounts and reports.

One person holds one role at a time. A person can change role while historical transactions remain intact.

## 3. Operating model

### Day lifecycle

A day is a shift. The store opens the register, sells all day, and closes the drawer at night.

The lifecycle is day open, then sales, then day close. A day close compares expected cash to counted cash and labels the result `BALANCED`, `SHORTAGE`, or `OVERAGE`. A close may include an optional physical count per product, with variance and notes, and a close statement print. A closed day is locked. A reopen requires approval, with a reason and an approver, and the prior close remains visible.

### Receiving

Stock enters only through purchases and receiving. A receiving record has a supplier, line items, unit cost, date, reference, and notes. Each line records a movement. A receiving starts as a draft and becomes submitted. Only a submitted receiving changes on-hand stock. A submitted receiving is locked. Reopening it requires authorization. All stock belongs to the one store warehouse.

### Customer credit, or utang

A customer may buy on credit. The system keeps named customers, an outstanding balance, a pay-down, and a ledger of every credit and payment. A credit sale creates a receivable and is excluded from expected cash.

### Corrections

Four correction types exist. Each requires a reason and, where the grant requires, an approver. Each creates a reversing movement. Original records remain visible.

- `VOID` cancels a sale.
- `RETURN` references an original receipt and never returns more than sold.
- `DAMAGE-LOSS` records damaged or lost stock.
- `ADJUSTMENT` records a manual change to stock.

A difference at day close is never corrected silently.

## 4. Reconciliation model

### Expected cash

```text
Expected cash = cash sales - cash refunds - approved cash expenses
```

Compare expected cash to counted cash.

- Counted equals expected: `BALANCED`.
- Counted is less than expected: `SHORTAGE`. The shortage is expected minus counted.
- Counted is more than expected: `OVERAGE`. The overage is counted minus expected.

A close statement carries the expected amount, the counted amount, the difference, and the label. The operator records notes.

### Expected on hand

An optional physical count applies to a day. For each product:

```text
Expected on hand = prior on hand + received - sold - damaged - lost - returned - adjusted
```

For each counted product, `variance = counted - expected`. A non-zero variance appears on the close statement with the expected amount, the counted amount, and the value of the variance. The operator records a reason for every variance.

### Non-cash and credit

Cash, GCash, Maya, and Bank Transfer sales are revenue. Only cash sales, cash refunds, and approved cash expenses affect expected cash. GCash, Maya, and Bank Transfer are non-cash and raise revenue without raising expected cash. A credit sale is utang and is a receivable, excluded from expected cash.

No variance is corrected without a recorded reason and an approver.

## 5. Functional scope

### Session

- Login and role picker with local username and password.
- Lock screen.
- Day open starts a register session. The day is open or closed.
- Reopen with approval, a reason, and an approver. The prior close remains.
- Sensitive actions record the actor, terminal, time, and reason.

### Catalog

- SKU, barcode, name, category, unit, cost, selling price, active flag, and created and updated timestamps.
- Barcodes are unique. A manual-sale product may have no barcode.
- An inactive product appears in historical reports but cannot be newly sold.

### Receiving

- Create a receiving record with supplier, line items, unit cost, date, reference, and notes.
- Draft, then submitted. Submit commits each line as a movement and raises on-hand stock.
- A submitted receiving is locked. Reopening it requires authorization.
- One store warehouse. All stock sits in that one location.

### Sales

- A barcode scan adds a product. A repeat scan increments the quantity.
- Manual search by SKU, barcode, or name.
- Cart: quantity change, line discount where permitted, subtotal, total, and optional customer.
- Payment methods: Cash, GCash, Maya, Bank Transfer, Credit and Utang, and Split.
- Cash computes change. Split payment lines equal the sale total.
- Checkout saves the sale, lines, payments, movements, and audit event atomically.
- The receipt prints after a successful save. A printer failure must not duplicate the sale. A sale is committed once.

### Corrections

- `VOID`, `RETURN`, `DAMAGE-LOSS`, and `ADJUSTMENT`, each with a reason and an approver where required.
- `RETURN` references the original receipt and never returns more than sold.
- Each correction creates a reversing movement. Original records remain visible.

### Day close and reports

- Expected cash versus counted cash, with a variance banner for `BALANCED`, `SHORTAGE`, or `OVERAGE`.
- Notes field.
- Optional physical count per product with variance.
- Close statement print.
- Reopen with approval, a reason, and an approver. The prior close remains.
- Reports: daily sales, payment breakdown, receivable, exceptions, inventory, and reconciliation.

### Import and export

- Catalog import matches columns by header name, shows a preview, validates rows, reports row errors, and writes an immutable import log that records the operator, timestamp, and source filename. An error report lists rejected rows without guessing a mapping.
- Exports: transaction detail, reconciliation, inventory, and receivable CSV. Each export uses RFC-4180, is invariant, and uses Asia/Manila dates.

### Store profile and first-run wizard

- Store profile: name, address, contact, and TIN. The profile supplies the receipt header.
- First-run wizard: store profile, admin account, and a printer and scanner test. The wizard runs on first launch.

### Devices

- USB barcode scanner, keyboard HID. Focus returns to the scan input after each scan.
- 80mm thermal printer, ESC/POS, 42 columns. The receipt is the source of truth.
- Cash drawer with kick. The drawer opens on checkout and on day-close print.
- Printer test, scanner diagnostics, and drawer test.

## 6. MVP inclusion list

The store POS MVP includes session and day lifecycle, catalog, receiving, sales, corrections, day close and reports, import and export, store profile and first-run wizard, and devices.

The MVP runs one store, one till, and one warehouse. Multi-till is future only.

## 7. Business rules

- Inventory never changes without a corresponding movement record.
- A sale cannot have zero lines, an inactive product, or insufficient stock unless an explicit role-based override exists.
- Sale numbers and receiving references are unique and human-readable. Internal IDs are GUIDs.
- Split payment totals equal the sale total. Cash change applies only to the cash component.
- A sale is committed once. Retrying a print must not create another sale.
- Voids and returns create reversing movements. Original records remain visible.
- Manual adjustments require a reason. Large adjustments require approval.
- A closed day is immutable except through a controlled reopen with approval.
- Money is integer Philippine peso centavos. Date boundaries use Asia/Manila.
- A difference at day close is labeled and recorded, never silently corrected.

## 8. Hardware profile

- Machine: one Windows machine at the store counter. 1366 by 768 minimum, 1920 by 1080 target.
- Printer: 80mm thermal, ESC/POS, 42 columns. Receipts fit the 42-column width.
- Scanner: USB barcode scanner, keyboard HID. Barcodes may carry leading zeros.
- Drawer: cash drawer with kick.
- Receipt: 80mm, 42 columns. Header from the store profile.

## 9. Non-goals

- No multi-till sync in the MVP. Multi-till is future only.
- No cloud. No network dependency.
- No loyalty program.
- No online payments. GCash, Maya, and Bank Transfer are recorded as payment methods. The point of sale does not connect to a provider and does not process a payment online.
- No promotion engine, no customer-specific pricing, and no commission.
- No route selling, no per-seller stock locations, and no outbound stock issuance to a seller.
- No GPS or route planning, no digital receipts, no Bluetooth or mobile version, and no direct integration with another point-of-sale system.

## 10. Conventions

- Money is integer Philippine peso centavos. The system stores exact minor units and never uses floating point for money.
- A date boundary uses Asia/Manila. A day is a day in Asia/Manila local time. The day close rolls over at midnight in Asia/Manila.
- Offline first. Login, sale, print, day close, report, and backup never wait for a network.
