# BG-005a independent audit — verdict & follow-ups

Audit: subagent 6e4fa7ea (independent 2nd reviewer, docs/00:134 auth trigger), head reviewed d99c882 (pre-fix2), verdict **PASS with conditions** (0 major / 8 minor / 3 notes).

## Fixed now (bg005a-fix2, audit-mandated)
- F1 atomic lockout increment (single-statement UPDATE, no read-modify-write under concurrency)
- F2 timing oracle: dummy-hash verify on unknown/locked/deactivated paths (generic messages unchanged)
- F6 last-admin/self-deactivation guard + reactivation clears auth state; AdminSeeder seeds only when Users empty (documented)
- F10 empty password = blank submit: no attempt, no counter, generic InvalidCredentials (comment made true)

## Documented follow-ups (do not fix now; owners noted)
- F3 counter+audit not one transaction — audit-failure divergence. Owner: BG-005d/auth-hardening round or 5b if it touches login. Acceptable today (single-process UI, IsBusy guard).
- F4 in-memory batch audits keep Sequence=-1 while DB assigns real identities; contract ILedgerStore:129-136 half-true. Owner: next identity-touch; either assign in-memory too or reword contract; add parity test.
- F5 migration Down restores renumbered (not original) audit sequences — ACCEPTED: originals recoverable from pre-migration backup (DbInitializer:40-43), renumbering is rowid-order deterministic; documented, no Down-data test.
- F7 RestoreService.VerifyAdminSeedAsync checks AppSettings admin row, not Users row. Owner: BG-005d (restore path) — verify Users admin + seed-on-restore.
- F8 account use cases take By from caller; IActorContext registered but no consumers. BY DESIGN until BG-005d binds session (decisions.tsv actor supply); binding MUST happen in 5d (service derives actor from IActorContext; cmd.By == CurrentUserId enforced).
- F9 AuthResult.AccountLocked reveals existence to rich clients — ACCEPTED by decision (UI generic; locked fires only after 5 attacks on existing account). Any future UI copy must stay generic.
- F11 case-sensitive username (SQLite BINARY) permits Admin/admin twins; consider NOCASE + OrdinalIgnoreCase on next auth touch. Owner: 5d.
- F12 terminal/reason only honored on auth rows; Reason never set; non-auth audits (batches, prints) leave them null. Owner: docs/02:100 category round (imports/exports/restores/config changes) — later slices.

## BG-001 deferred findings 3+4 — status
- CLOSED (auth core): login success/failure audit every attempt, generic no-reveal copy (F2 timing caveat fixed), lockout 5/15, 3 roles + capability gates.
- REMAINING (background): docs/02:100 categories for overrides/imports/exports/restores/config-changes + print audits already exist; terminal/reason on non-auth rows — carried as follow-ups above.
