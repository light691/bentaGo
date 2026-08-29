# BG-005 plan (auth, catalog, trips, stock loads, CSV exchange)

Blueprint: docs/05 lines 287-339. Depends on BG-003 (merged) + BG-004 (merge gate, operator UI rework in progress).

## Slices (each <=2 commit milestones, briefs inline every requirement)

1. **BG-005a identity + auth + audit** (no shell screens): users/roles/peddler entities, local password hashing (PBKDF2, salt), lockout policy, role checks (admin/peddler), login service, command/use cases, AuditEvent entity + persistence + writes for login success/failure (+ import/load commits land in later slices). Fixes deferred BG-001 audit findings 3+4 (auth/audit logging) - re-derive exact requirement from docs/00 audit-trail section + docs/05 lanes 1-2 ("successful audit event", "attempt is audited"); record derived exact schema in decisions.tsv. Re-audit at slice end.
2. **BG-005b catalog products + CSV v2** (no shell screens): product entity (sku, barcode, name, unit, retail+wholesale price, category, expiry, discount flags), CSV parser at boundary (RFC 4180 quoting, encoding UTF-8 BOM ok, delimiter detection, leading zeros preserved, money 2-decimal -> centavos, date M/D/YYYY culture-explicit), typed preview rows, atomic batch commit, immutable import receipts + row-specific error files, import history, export (stable ids, references, totals, encoding per contract). Owner decision: BG-005c owns stock_qty negatives (reject = row error per v1 contract).
3. **BG-005c trips + stock loads + peddler isolation**: trip creation (per peddler, active trip, location), load header + stock-load-lines-v1.csv, approval before commit, availability check (reject insufficient), isolated inventory views per peddler (prove peddler sees only assigned stock).
4. **BG-005d admin views** (AFTER BG-004 merge; src/BentaGo.App/Features/Admin/ + VM): admin login, product import UI, peddler/trip admin, stock load UI. Screens must follow the post-rework shell design system (Codex rework lands first).

## Client Product.csv -> v2 contract design (draft, decisions needed)

v1 products (sku,barcode,name,unit,price) has NO sku/unit and only one price; client file = 17 cols:
`name,description,Category,rp_inclusive,wp_inclusive,barcode1,barcode2,Warehouse,s_discR,s_discPWD_SC,s_PWD_SC_perc,s_discAth,s_ask_qty,stock_qty,cost,has_expiry,expiration_date`.

Proposed `products-v2.csv` (additive to v1, never silent reinterpret):
- Columns: `sku,barcode,name,unit,price,price_wholesale,category,has_expiry,expiration_date,discount_pwd_perc,cost` (draft - finalize in brief).
- Mapping from client: name=name; description dropped (inferred note) or kept as v2 optional col; unit derived: client has none -> default per category map (draft: canned/pasta/etc -> can/pack; else "piece"); price=rp_inclusive; price_wholesale=wp_inclusive (0 => null); barcode=barcode1 (barcode2 secondary; leading zeros preserved; optional unique); category=Category (raw, mixed-case brands => keep raw + normalization map later); has_expiry/expiration_date direct (date parse explicit M/D/YYYY); discount_pwd_perc=s_PWD_SC_perc + s_PWD_SC flag -> v2 dict? (draft: boolean pwd_eligible + perc); cost=cost.
- **sku derivation (DECISION)**: client has no sku; propose deterministic: barcode present -> `-{barcode1}` prefixed token? or `SKU{barcode1}`; no barcode -> normalized name hash, max 24 alnum. Operator to approve.
- **Negative stock_qty (DECISION)**: v1 contract rejects negative quantity; recommend reject with row error "negative stock" (client's -21.00 rows are over-sell history; fix in source or load via opening-balance adjustment later).
- Perf probe: 50,000 rows preview <=3s, commit <=5s (BG-005b owns; baseline = parser-only on main first).

## Verification per docs/05
unit: `dotnet test -c Release --filter "Auth|Catalog|Import|StockLoad"`; live: 10 lanes (keyboard harness; Computer-Use adapter produced) at head; perf: 50k probe + 5 interleaved imports, fresh DB each. Review gate: screens + bg-005-admin.mp4, stop at operator click.

## Explicit non-goals / quarantine
- NO src/BentaGo.App screen code before BG-004 merges (Codex UI rework owns shell; one-writer rule).
- NO changes to shared WPF resources (Theme.xaml, ScreenStyles.xaml) until rework merges.
- Full client Product.csv stays out of Git (order 14); a schema-sampled synthetic fixture + profile notes ship with the unit.
