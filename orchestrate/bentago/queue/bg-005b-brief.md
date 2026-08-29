# BG-005b — catalog + client CSV import (domain, no UI)

Status: READY TO DISPATCH (after BG-005a merges). Owner lane: deepseek (domain). UI wiring (import screen/preview) is GLM territory in BG-005d — this slice is service + persistence only: NO src/BentaGo.App changes, NO documents/ changes.

## Source of truth
- Client file: C:\Users\nicky\Downloads\Product.csv (5,560 rows, 17 cols, NO BOM, CRLF). Order 14: file stays OUT of Git. Schema-sampled synthetic fixture (20-40 rows incl. negative/empty/quote/dup cases) ships IN repo under tests fixtures.
- Known profile: cols name,description,Category,rp_inclusive,wp_inclusive,barcode1,barcode2,Warehouse,s_discR,s_discPWD_SC,s_PWD_SC_perc,s_discAth,s_ask_qty,stock_qty,cost,has_expiry,expiration_date. rp_inclusive populated 5,553/5,560 (7 empty); wp_inclusive 4,821>0 / 739=0; barcode1 4,667/5,560 EAN-13; barcode2 7 rows; names unique 5,560; 81 categories (mixed case); stock_qty 3,451 negative + 450 zero; money 2dp; expiry M/D/YYYY; 7 rows contain quote chars.

## LOCKED DECISIONS (decisions.tsv, do not re-litigate)
1. SKU derivation (deterministic, in client-mapping layer): primary = barcode1 digits verbatim when barcode1 present and non-empty; fallback = "X" + first 12 hex of SHA-1(normalized name: trim, collapse whitespace, uppercase invariant). Digit-vs-X-prefix = collision-free by construction. Derived SKU ALWAYS shown in preview. Collision (derived SKU already exists in catalog OR duplicates within file) = SPECIFIC per-row error, row excluded, import continues; empty name = row error.
2. stock_qty is NOT imported (3,451 negative = junk); preview shows it column-wise as informational only; inventory state comes from movements (BG-005c).
3. barcode2 dormant: not imported, not stored; documented as future.
4. v2 product contract is ADDITIVE to v1. Mapping: name=name trim, description, category trimmed (no case normalization — brands live in Category; keep as-is), retail=rp_inclusive (7 empty => null + flagged row warning NOT error? — NOTE: empty retail on an active file row = row ERROR per data-quality review: empty price is invalid for selling; decide: rp empty => row error), wholesale=wp_inclusive (0 => null), unit = category-default ("piece"), expiry = expiration_date parsed with explicit culture (M/D/YYYY), has_expiry bool drives expiry-tracking flag, PWD/Senior flags: derived = s_discPWD_SC/s_PWD_SC_perc/s_discAth present-and-nonzero => supports PWD/senior discounts (store raw values in import preview only; 5d wires policy), cost as decimal.
5. Validation errors: per-row error list (empty name/price, bad money, bad date, dup/collision sku, bad row shape); whole-file ok/error summary; import is all-or-nothing per successful rows? NO — rows with errors excluded, valid rows import (idempotent via sku upsert? DECIDE: contract = UPSERT on sku for re-imports (client re-sends file), document).
6. Audit/ledger: NO RecordAudit usage in 5b (imports category = later slice per docs/02:100 round).
7. Contract v2 sku field REQUIRED (derivation in mapping layer satisfies "present"; no sku column in file).

## Deliverables (2 commits, same two-milestone pattern)
- Commit 1 (Core+Application): Product record v2 (additive), Category value (already exists? check), SKU type? (keep string with validation in Application), CsvProductFile model, mapping + derivation pure functions (Application/Import), validation, ImportPreview DTO (rows + errors + summary), IProductImportService (preview(file) -> PreviewResult).
- Commit 2 (Infrastructure+tests): ProductStore upsert-by-sku, import service implementation (parse via TextFieldParser-compatible manual parser — NO BOM tolerance + quote-shape handling: 7 rows contain quotes), fixture CSV, parser unit tests (quotes, empty, negatives, dates, money), derivation tests (barcode present/absent, name normalization, collision), mapping tests (retail empty, wholesale 0, category defaults), preview summary tests, integration test: fixture -> preview -> import -> catalog rows match golden expectations; idempotent re-import (same sku upsert).

## Boundaries
- NO App/XAML/doc changes. NO migrations unless additive DDL needed (prefer contract-only + store-level upsert; if schema needs change, it's a NEW migration and 3a-style tests).
- One writer: bg-005b worktree; explicit-path commits only.
- Acceptance: build 0 warnings; my chain 143+new green; golden import run keeps persisted-root byte-stable? (Import writes catalog rows -> RunGoldenReconcile expectations already include product rows? Check tests: catalog products are seeded; import test uses isolated root.)
