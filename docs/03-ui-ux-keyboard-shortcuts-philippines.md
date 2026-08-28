# BentaGo UI/UX, Page Designs, Keyboard Shortcuts, and Philippine Conventions

## 1. Design principles

The selling screen is the product’s centre of gravity. It must work offline, with a scanner, a small thermal printer, and a keyboard. Use large totals, strong focus indication, high contrast, simple language, and visible status. Keep admin tools separate from the peddler flow.

Use Philippine peso formatting such as `₱1,250.00`, dates in `28 Aug 2026` for display, and Asia/Manila local time. Avoid assuming every customer has a barcode or digital payment. Keep Cash, GCash, Maya, Bank Transfer, and Credit/Utang first-class choices.

## 2. Navigation map

```text
LOGIN
 ├─ Peddler Home
 │   ├─ New Sale
 │   ├─ My Stock
 │   ├─ Transactions
 │   ├─ Returns / Damage
 │   └─ End Trip
 └─ Admin Home
     ├─ Today
     ├─ Products / Import
     ├─ Peddlers / Users
     ├─ Stock Loads
     ├─ Trips / Reconciliation
     ├─ Reports / Export
     └─ Settings / Backup / Hardware Test
```

## 3. Page designs

### Page 1 — Login

Centered card: BentaGo logo/name, username, password, `Log in`, database/terminal status, and version. On failure show a plain reason without revealing whether a username exists. Allow Enter to submit and F1 to open help.

### Page 2 — Peddler Home

Header: greeting, peddler name, trip number, online/offline indicator (informational), and current time. Large cards: Today’s Sales, Transactions, Cash Sales, and Remaining Items. Primary button: `NEW SALE`. Secondary actions: My Stock, Transactions, Returns, End Trip. Show an urgent banner for an open trip from a previous date.

### Page 3 — New Sale / POS

```text
┌──────────────────────────────────────────────────────────────┐
│ BentaGo  Trip: TRIP-20260828-001  Juan       [F4 Hold] [F12] │
├──────────────────────────────┬───────────────────────────────┤
│ SCAN BARCODE / SEARCH        │ CART                          │
│ [ focused input             ] │ Coke 1.5L     2 × ₱65  ₱130 │
│                              │ Sardines       1 × ₱28   ₱28 │
│ Results / product cards      │                               │
│ SKU · name · price · stock   │ Subtotal              ₱158.00 │
│                              │ Discount                ₱0.00 │
│ [Manual item] [Clear search]│ TOTAL                 ₱158.00 │
│                              │ [CHECKOUT] [CANCEL]          │
└──────────────────────────────┴───────────────────────────────┘
```

Scanning adds or increments an item and keeps focus in the scan field. A missing barcode shows a short actionable message and preserves the typed value. Checkout opens a payment dialog, not a new page.

### Page 4 — Payment dialog

Show total at top. Tabs/buttons: Cash, GCash, Maya, Bank Transfer, Credit/Utang, Split. Cash shows `Amount received`, `Change`, and numeric keypad. Split shows payment lines and remaining balance; completion is disabled until the balance is zero. Credit requires customer/reference details according to the client’s policy. Confirm with Enter; Esc returns to cart.

### Page 5 — Receipt result

Show `Sale saved — Receipt 000123`, payment summary, print status, and buttons `Print`, `Reprint`, `New Sale`, `View Sale`. If printing fails, say “Sale saved. Printer needs attention.” Never ask the user to checkout again.

### Page 6 — My Stock

Searchable table: SKU, product, loaded, sold, damaged/lost, returned, on hand, and last movement. A compact “low stock” filter is useful, but no automatic replenishment is required for MVP.

### Page 7 — Stock Load (admin)

Step layout: Select peddler → add/import lines → validate → review total/value → approve/commit → print load sheet. Show Main Store available quantity beside each line. Never allow an unreviewed CSV to alter stock.

### Page 8 — Transactions

Filters: date, trip, peddler, receipt number, payment method, status. Detail view shows sale lines, payment lines, print status, void/return history, and audit events. Actions are permission-aware.

### Page 9 — End Trip / Reconciliation

Top summary: gross sales, discounts, net sales, cash, GCash, Maya, bank transfer, credit, refunds, and expected cash. Input: actual cash remitted, cash expenses if enabled, notes. Optional inventory count table: expected, counted, variance, reason. A large result banner says `BALANCED`, `SHORT ₱100.00`, or `OVER ₱100.00`. Buttons: Save Draft, Submit Close, Print Summary. Closed state is visibly locked.

### Page 10 — Reports

Use a date range and peddler/trip filters. Provide preview, print, and CSV export. Default reports: daily sales, payment summary, peddler comparison, inventory movement, load vs sold vs remaining, and exception/variance report.

### Page 11 — Settings and diagnostics

Tabs: printer, scanner, receipt layout, backup, import/export folders, users/roles, and application information. Include `Print Test Receipt`, `Scan Test`, `Open Data Folder`, `Create Backup`, and `Restore Backup` with strong confirmation.

## 4. Keyboard shortcuts

Shortcuts must be displayed in tooltips and a Help overlay. Avoid intercepting standard text-editing keys inside text fields.

| Shortcut | Action | Where |
|---|---|---|
| `F1` | Help / shortcut list | Everywhere |
| `F2` | Focus barcode/search input | POS |
| `F3` | Product search | POS |
| `F4` | Hold/suspend current cart | POS |
| `F5` | Refresh current data | Lists |
| `F6` | Focus payment method | Payment |
| `F7` | Apply permitted discount | POS |
| `F8` | Open returns/adjustments | POS/admin |
| `F9` | Open today’s transactions | POS |
| `F10` | Open end-trip/reconciliation | Peddler home |
| `F12` | Checkout | POS |
| `Enter` | Confirm / select highlighted item | Contextual |
| `Esc` | Close dialog / cancel current edit | Contextual |
| `Delete` | Remove selected cart line after confirmation | POS |
| `+` / `-` | Increase/decrease selected quantity | POS |
| `Ctrl+N` | New sale | POS |
| `Ctrl+P` | Print/reprint current document | Documents |
| `Ctrl+S` | Save draft | Forms |
| `Ctrl+F` | Focus search | Lists |
| `Ctrl+R` | Return to current screen’s default state | Safe screens |
| `Alt+1` | Home | Main navigation |
| `Alt+2` | New sale | Main navigation |
| `Alt+3` | Stock | Main navigation |
| `Alt+4` | Transactions | Main navigation |
| `Alt+5` | Close/reconcile | Main navigation |
| `Ctrl+Alt+L` | Lock screen | Everywhere |
| `Ctrl+Alt+B` | Create backup (admin) | Admin |
| `Ctrl+Alt+T` | Hardware test (admin) | Admin |

Do not use `Ctrl+C`, `Ctrl+V`, `Ctrl+X`, or standard numeric keypad behavior for custom actions. Barcode scanner Enter must submit the focused scan input but must not accidentally confirm a payment dialog unless the user is at the intended confirmation step.

## 5. Philippine operating conventions

- Currency: Philippine peso, display `₱`; store exact minor units.
- Common payment labels: Cash, GCash, Maya, Bank Transfer, Credit/Utang, Split Payment.
- Credit sales should capture customer name and reference/contact only if the client approves this practice; do not assume a full customer-ledger feature in MVP.
- Use product names and units familiar to the store; support piece, pack, bottle, can, case, kilo, and other configurable units.
- Preserve leading zeros and long numeric barcodes during CSV import.
- Support Filipino/English wording in configurable labels, but keep system statuses unambiguous.
- Receipts should include store name/address/contact/TIN only when supplied by the client. Do not invent tax registration details.
- Include a receipt note area for “Thank you” or store-specific text, configurable by admin.
- Make the system tolerant of intermittent connectivity: all selling functions remain local and never block on internet.

## 6. Receipt layout

Thermal receipt should include store header, date/time, peddler, trip, receipt number, item lines, quantity, unit price, discounts, total, payment breakdown, change, and a return/void reference policy. Keep a machine-readable internal ID in a small footer if useful for support. Reprints must be labelled `REPRINT`.

## 7. Accessibility and error copy

Use keyboard-visible focus, minimum readable font sizes, color plus text for status, and do not rely on red/green alone. Error copy should answer what happened and what to do next: “Barcode not found. Search by product name or ask admin to import it.”; “Printer unavailable. The sale is saved; reconnect it and choose Reprint.”

## 8. WPF implementation mapping

The preferred visual reference is `designs/bentago-wpf-mvp-screen-set.png`. It is deliberately constrained to the locked C#/.NET 10/WPF/MVVM stack rather than a web UI.

| Visual element | WPF implementation |
|---|---|
| Main window, left rail, content area, status bar | `Window` + `Grid`/`DockPanel` + `ContentControl` navigation region + `StatusBar` |
| Page navigation | MVVM navigation service; commands bound to buttons and `KeyBinding`s |
| Sales cart, stock, transactions, variance views | `DataGrid` with read-only/display columns and row-level commands |
| Search and barcode focus | `TextBox` bound to scanner input; Enter command processes HID scanner text; explicit focus restoration after each scan |
| Payment and confirmation overlays | WPF modal `Window`/dialog service, not a browser overlay |
| Function-key labels and keyboard-first flow | `RoutedCommand`/MVVM commands with `KeyBinding`; maintain an in-app shortcut help overlay |
| Buttons, field validation, warning banners | Native WPF controls with reusable styles, converters, and validation templates |
| Local/offline status | View-model state from local SQLite/backup/printer services; no network required for a sale |
| Receipt status/reprint | Checkout commits through EF Core first; printer adapter reports status after commit; reprint is a separate command |

Avoid assuming web-specific mechanisms such as client-side routing, browser storage, service workers, responsive breakpoints, or a remote API in MVP. The visual language can be modern, but the implementation is a Windows desktop app using reusable WPF styles and controls.
