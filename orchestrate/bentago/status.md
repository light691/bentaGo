# BentaGo program status

Derived from units.tsv and ledger.tsv at each drain. Do not hand-maintain.

## Queue

| Unit | Track | State | Branch | Head | Verdict |
|---|---|---|---|---|---|
| BG-001 | foundation | MERGED | main | 716c95f | 12/12 + foundation verify PASS |
| BG-002 | domain | MERGED | main | 1e82759 | 43/43, 2 audit rounds, golden scenario |
| BG-003 | persist | MERGED | main | b65469a | 59/59 + verify PASS |
| BG-004 | shell | MERGE-GATE | bg-004-glm7 | c6bf973 | 127/127; review pack PASS rail-gated; operator UI rework (Codex) in progress |
| BG-005 | admin | NEXT | bg-005 | - | auth, catalog, trips, loads, CSV exchange; after BG-004 merge |
| BG-006 | sales | pending | bg-006 | - | - |
| BG-007 | hardware | pending | bg-007 | - | - |
| BG-008 | close | pending | bg-008 | - | - |
| BG-009 | release | pending | bg-009 | - | - |

Dependency graph (docs/05): BG-002 and BG-004 after BG-001. BG-003 after BG-002. BG-005 after BG-003 and BG-004. BG-006 after BG-005. BG-007 after BG-006. BG-008 after BG-005, BG-006, BG-007. BG-009 after BG-008.

## Gates

1. Remote destination: CLOSED (repo live: github.com/light691/bentaGo, gh v2.98.0 auth light691; PR lifecycle operational).
2. Hardware: standard PH picks locked (Zebra DS2208, Epson TM-T82 80mm); client confirmation pending before BG-007 release lanes.
3. Client CSV samples: SATISFIED 2026-08-29 (Product.csv, 5,560 products) -> BG-005+ product-import unit; full file stays out of Git; schema-sampled fixture ships with the unit.
4. Live-lane control path: adapter in production (keyboard SendInput harness + verify-bentago skill; docs/05 "Computer Use" lanes run via keyboard driver); flag for strategy check before BG-005/006 lane rounds.
5. BG-004 interaction-change gate: open, waiting operator click (Codex UI rework in progress).

> 2026-08-29: Client catalog CSV received (Downloads\Product.csv, 5,560 products). Gate 3 satisfied. Full file stays out of Git (stand order 14) - a schema-sampled fixture ships with the unit.
