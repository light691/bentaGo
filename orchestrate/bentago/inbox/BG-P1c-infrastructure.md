BG-P1c: Infrastructure + schema reset to ONE baseline migration
Status: queued (launch after P1b merges)
Author: coordinator, 2026-08-30

PREREQS
- BG-P1a Core merged: day lifecycle documents, movement kinds
  GoodsReceived / FundIn / FundOut, day-keyed fold with opening float
  (ADR 0004).
- BG-P1b Application merged: contracts and services retargeted to the day
  domain (orchestrate/bentago/inbox/BG-P1b-application.md).
- Read orchestrate/bentago/preferences.md, docs/05 BG-P1 box, ADR 0003
  (BIR readiness), ADR 0004.

SCOPE (BG-P1 owns Infrastructure — standing order 11 supersedes the old
stage boundary that excluded it)
1. DELETE the 8 existing EF migrations. Produce ONE baseline migration that
   creates the target schema in full (day documents, receiving-ready shape,
   customers-ready shape is NOT in scope — P3 adds those).
2. BIR readiness fields from ADR 0003, in this baseline: sale/receipt
   carries serial_number (long, unique, gapless sequence backed by a
   dedicated sequence table or identity strategy the fold can audit),
   terminal_id (string, from Environment.MachineName via the audit path),
   machine_serial reserved column. Gaps impossible; voids consume the
   serial (no reuse).
3. Schema reset means the store's data is disposable for this pivot: no data
   migration from the old trip-era tables. Document that in the migration
   header comment.
4. Update the DbContext configuration to the new Core shapes; indexes for
   day-keyed queries (day key, receipt serial).
5. Appendix triggers proof from docs/05 (whatever the plan's BG-P1 verify
   box names for persistence integrity) still holds on the baseline.

ACCEPTANCE
- dotnet build -c Release 0/0; dotnet test -c Release green for Core +
  Infrastructure buckets; dotnet format clean.
- `dotnet ef migrations list` (or the project's equivalent) shows exactly
  ONE migration.
- A fresh database creates and opens; a Day can open with a float, take a
  FundOut expense, and close balanced in an integration test.
- Grep gate: no Trip/Peddler identifiers in src/BentaGo.Infrastructure.

WORKFLOW
- Worktree D:\bentago\.wt\bgp1c, branch bgp1c from origin/main. Push when
  green; coordinator opens PR and squash-merges (standing order 13).
COMMIT
BG-P1c: schema reset to one baseline migration with BIR serial + terminal identity
