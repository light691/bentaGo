# BG-005d-core — session actor binding + auth hardening (domain, no UI)

Status: READY TO DISPATCH. This is the CODING part of BG-005d; the ADMIN VIEWS (5d-ui) wait for the operator's ui-rework to land. NO src/BentaGo.App changes.

## Scope (each item from the BG-005a audit follow-ups, queue/bg-005a-audit-followups.md — read it first)
1. **F8 (mandatory, audit-flagged): bind account use cases to the session actor.** UserAccountService gates currently read `cmd.By` (caller-supplied → any in-process caller can spoof an admin's UserId). Fix: services derive the actor from IActorContext (registered by BentaGoHost since 5a, currently zero consumers) and reject `cmd.By != CurrentUserId` (DomainError, e.g., AUTH_ACTOR_MISMATCH). Callers (tests, future admin UI, tools) must pass the session user. IActorContext needs a CurrentUserId surface — check its shape; wire ActorContext to the live login session (LoginService.OnSuccess sets it; host registers as singleton — decide the lifetime semantics carefully: the ACTOR is the LOGGED-IN user; host must set it on login success and clear on logout; tests that don't log in use an explicit context override/seam).
2. **F3: auth-state write + audit atomicity.** LoginService persists the counter/state then audits separately (two transactions; audit failure leaves divergence). Fix within the existing architecture: either (a) route the state update + AuditEvent through ONE unit of work (the ledger batch API accepts audit events — see how RecordAudit works with the in-memory/sqlite mirrors), or (b) explicit compensation (document). Must keep: audit every attempt; lockout semantics unchanged; in-memory and SQLite parity.
3. **F7: RestoreService.VerifyAdminSeedAsync checks only the AppSettings seed row** — must ALSO verify the Users table has the admin row (BG-005a shape); mismatch = restore verification failure with a clear message; keep the existing AppSettings checks (legacy rows still seeded).
4. Minor cleanups (only if trivial, else record): F10 already fixed in 5a; F11 username case sensitivity (NOCASE index + OrdinalIgnoreCase lookups) — only if the migration is cheap and 3a-style tested; F4 in-memory batch-audit identity parity (Sequence -1) — reword the ILedgerStore contract comment OR assign identities in-memory (prefer the cheap contract-wording fix; a parity test if behavior kept).

## Deliverables (2 commits, milestone-split, explicit paths)
- Commit 1 (Core+Application): IActorContext enrichment (CurrentUserId + session lifecycle), UserAccountService actor binding + AUTH_ACTOR_MISMATCH, tests: admin-By-spoof rejected, supervisor/peddler rejected, self-password-change via session works, login sets + clears session actor. Result<T> pattern per 5a.
- Commit 2 (Infrastructure+tests): LoginService atomicity fix (or documented compensation), RestoreService Users-row verification + tests (schema-valid backup missing Users admin row now FAILS verification; BG-005a-era backup passes), contract wording for F4 + parity test if behavior kept, migration ONLY if F11 approved (default: NO migration in this slice; F11 recorded if it needs one).

## Boundaries
- No App/XAML/docs changes. 5d-ui (admin screens: user management, catalog import view, trips/admin overview) is a SEPARATE slice after the ui-rework merge — do not stub App code.
- One writer worktree .wt/bg-005d (create from current main).
- Acceptance: build 0 warnings; coordinator chain (build + test + format verify); existing 298 + new green; goldens unaffected (RunGoldenReconcile byte-stable — the two-peddlers/trip-lifecycle scenario outputs must not change; if F8/F3 touches the scenario path, scenarios must still render identically).

## Standing rules
- Follow the docs contracts; make the conservative documented choice on silence; never ask mid-flight; explicit-path commits; no history rewrite; build only, report SHAs.
