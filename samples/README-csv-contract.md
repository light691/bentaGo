# BentaGo CSV contract v1 (coordinator sample fixtures)

**Status:** v1 samples authored by the coordinator (operator decision 2026-08-29). Client will supply real main-POS exports later; those become at least v2 and remain a release gate. The parser treats v1 as the versioned contract: version changes are additive, never silently reinterpreted.

## General rules (all files)

- Encoding: UTF-8 (BOM accepted and stripped; no UTF-16, no local codepage).
- Delimiter: comma. No quoted-field tricks beyond standard CSV quoting (RFC 4180).
- Header row required; column order fixed per version; unknown extra columns are rejected, not ignored.
- SKU is the stable business key (Gate 0 row 2). Barcode optional, unique when present, always text (leading zeros preserved).
- Money columns are pesos with at most 2 decimals (`65.00`). The parser converts to integer centavos. 3+ decimals rejected.
- A batch imports atomically. Any invalid row rejects the whole batch with a row-specific error file.
- Row errors are specific (SKU duplicate, missing name, bad barcode shape, negative quantity, 3-decimal price, unknown SKU), never silent corrections.

## products-v1.csv

Columns: `sku,barcode,name,unit,price`

- sku: required, alphanumeric (0-9A-Za-z-), max 24 chars.
- barcode: optional, 6-20 digits, digits only, leading zeros significant, unique in batch.
- name: required, 1-100 chars.
- unit: required, per-unit label (bottle, can, piece, kg, pack).
- price: required, 0.01 to 999999.99, at most 2 decimals.

Sample rows use typical Philippine route-selling products with market-plausible prices.

## stock-load-lines-v1.csv

Columns: `ref,sku,quantity`

- ref: required load reference (e.g. LOAD-2026-0001); lines with the same ref belong to one load.
- sku: required, must exist in the catalog at import time (or rejected per row).
- quantity: required integer 1-999999.

The load header (peddler, date, approvals) is created in-app; this file supplies the lines.

## Error fixtures

`samples/input/errors/products-v1-errors.csv` contains exactly these defects, one per row, each with an expected specific error:
1. duplicate barcode (same digits as row 2)
2. missing sku
3. 3-decimal price (65.005)
4. negative price
5. empty name
6. barcode with a letter (not digits)
7. unknown column (extra 4th column) -> whole file rejected, not per-row
