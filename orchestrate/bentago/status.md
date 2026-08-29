# BentaGo program status

Derived from units.tsv and ledger.tsv at each drain. Do not hand-maintain.

## Queue

| Unit | Track | State | Branch | Head | Verdict |
|---|---|---|---|---|---|
| BG-001 | foundation | pending | bg-001 | - | - |
| BG-002 | domain | pending | bg-002 | - | - |
| BG-003 | persist | pending | bg-003 | - | - |
| BG-004 | shell | pending | bg-004 | - | - |
| BG-005 | admin | pending | bg-005 | - | - |
| BG-006 | sales | pending | bg-006 | - | - |
| BG-007 | hardware | pending | bg-007 | - | - |
| BG-008 | close | pending | bg-008 | - | - |
| BG-009 | release | pending | bg-009 | - | - |

Dependency graph (docs/05): BG-002 and BG-004 after BG-001. BG-003 after BG-002. BG-005 after BG-003 and BG-004. BG-006 after BG-005. BG-007 after BG-006. BG-008 after BG-005, BG-006, BG-007. BG-009 after BG-008.

## Gates open

1. Remote destination unnamed. PRs are local branches; gh blocked.
2. Real hardware facts before BG-007 release lanes.
3. Client CSV samples before release.
4. Computer Use control path for WPF live lanes. Blocked lanes fail closed.

> 2026-08-29: Client catalog CSV received (Downloads\Product.csv, 5,560 products). Gate 3 (CSV samples before release) satisfied; scoped as a BG-005+ product-import unit. Full file stays out of Git (stand order 14) - a schema-sampled fixture ships with the unit.
