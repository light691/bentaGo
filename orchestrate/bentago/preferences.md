# BentaGo standing orders (paste verbatim into every worker brief)

1. Repo root is C:\BentaGo. Work only inside the assigned package boundary and listed ownership. Do not touch files owned by another package.
2. Read docs/00 through docs/03 before editing. docs/04 defines the packages. The master plan in docs/00 is the source of truth for gates, milestones, evidence, and review triggers.
3. Locked stack. .NET 10, WPF, MVVM, SQLite, EF Core, keyboard-HID scanner abstraction, ESC/POS printer abstraction, CSV exchange, Windows x64, Velopack. No cloud sync, no web API, no direct main-POS database integration, no unapproved third-party UI frameworks.
4. Machine has no .NET 10 SDK in PATH. Use C:\Users\nicky\.dotnet\dotnet.exe explicitly, or prepend C:\Users\nicky\.dotnet to PATH inside the command. Never rely on the system dotnet 8.
5. Money is integer minor units (Philippine centavos). Storage timestamps are UTC. UI displays Asia/Manila. Quantities are integers unless a gate record says otherwise.
6. Inventory movement is append-only source of truth. InventoryBalance is a rebuildable cache. Every movement references its business document and actor. No destructive edits, no hard deletes.
7. Checkout is one database transaction. Printing happens after commit. A failed print never rolls back or duplicates the sale. Idempotency key on every sale submission.
8. Verification bar. Behavior changes need unit or integration tests that run and pass. `dotnet build` must succeed and `dotnet test` must pass before a unit is reported done. "It compiles" is not evidence for accounting, inventory, import, printer, or reconciliation work.
9. One agent owns the solution/projects/migrations at a time. Two agents never edit the same project file or migration simultaneously.
10. Git. Branch per package: pkg/<package-letter>. Commit per unit with a clear message. Never push or force-push. Merge is done by the coordinator only. Do not merge or alter another agent's unreviewed work.
11. Evidence per docs/00 section 7 and the handoff template in docs/04. Screenshot required for each material WPF screen change.
12. High-risk work (ledger semantics, money, checkout, idempotency, imports, authz, migrations, restore, reprint, close/reopen) triggers an independent reviewer pass before the unit lands, per docs/00 section 8.
13. New requests that are not in scope become follow-ups. MVP scope per docs/00 section 2 is frozen. A V2 idea goes to the backlog note, never into a task.
14. If a brief says something ambiguous, resolve it from the docs in this order (00, 01, 02, 03, 04), then from an explicit earlier coordinator note. Never invent business rules.
15. Timebox. If the timebox expires, return partial findings and stop. Report exactly what ran, what failed, and what remains.
