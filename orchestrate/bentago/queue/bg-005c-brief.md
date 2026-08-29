# BG-005c — trips, stock loads, peddler isolation (domain, no UI)

Status: READY TO DISPATCH. Owner lane: deepseek (domain). NO src/BentaGo.App changes, NO docs/ changes; UI wiring (admin screens, 5-state trip UI) belongs to BG-005d.

## Scope (docs/05 287-339 + docs/00 trip/load contracts)
- Trip lifecycle: 5 states. Check the current Trip model and the OpenTripService (IOpenTripService already exists) — confirm or extend to the full state machine: OPEN -> (loaded) ACTIVE? -> CLOSED, with each transition audited (AUTH/Trip audit categories exist as AUTH.* — trip events use ledger audit categories per docs/02:100; CHECK the existing AuditEvent categories and use TRIP.* if they exist, else document + use the closest existing category consistently).
- One active trip per peddler (docs/00:47): the invariant must be enforced in the domain/service (not just UI): opening a new trip while another is OPEN/ACTIVE for the same peddler = DomainError, no double-open possible.
- CancelTrip: cancelled trips don't create stock deltas; cancelled-after-load = loads reverted (state machine must say whether load reverts on cancel — DECIDE per contract; if docs are silent, make the conservative documented choice: cancel of a loaded trip reverts its loads, all in one unit of work).
- Stock loads: adding stock = movement (LEDGER.MOVEMENT?) with Load/Unload types — reuse the existing movement type set; negative-stock reject = ROW ERROR per the v1 contract (docs/05 v1: negative stock = row error, not a warning) — CONFIRM against the live ILedgerStore.Scan/LoadAPI and the existing movement categories.
- Peddler isolation: a peddler sees/affects only their own trip's rows: stock visibility + reconciliation scoping — the domain services must take the peddler id (or an ActorContext) and reject cross-peddler access to trips/loads (DomainError, not silent).

## Locked decisions (from decisions.tsv; do not re-litigate)
- Trips/5-state/CancelTrip/one-active-trip are BG-005c (recorded in BG-005a design round).
- Stock_qty import stays informational (5b); inventory state comes FROM MOVEMENTS — 5c must expose inventory-derived-from-movements (the read model that shows "on hand" per SKU, driven by ledger movements, not a stored stock_qty).
- Auth/actor: IActorContext exists (from 5a); 5c may bind trip/load services to actor = current user's PeddlerId (the F8 by-vs-actor binding homework is 5d for ACCOUNT services; for trip/load services, PASS THE PEDDLER ID explicitly through service methods if the actor wiring is not yet safe; document).
- No RecordAudit for 5c unless the trip transitions REQUIRE audit per docs/02:100 (check: category exists? then yes: record CLOSE/LOAD/etc. per existing category set).

## Deliverables (2 commits, milestone-split, explicit paths)
- Commit 1 (Core+Application): trip state machine (pure), CancelTrip/auto-cancel semantics, one-active-trip invariant in service layer, peddler-scoping rules, unit tests for every state transition (incl invalid transitions, cancel-after-load, double-open-rejected, cross-peddler-rejected).
- Commit 2 (Infrastructure+tests): load/unload movements wired through ILedgerStore against the EXISTING schema (no new migrations unless the trip states need persistence columns — if they do, PREFER reusing existing columns; new migration only if unavoidable and then 3a-style tests), peddler-scoped read model (on-hand per SKU from movements), integration tests (open->load->sell->close golden run on an isolated root + RESTART/REBUILD replay stays byte-stable), scenario tool extension: add a 'trip-lifecycle' scenario (open, loads, cancel, exception rows) similar to two-peddlers with GOLDEN/REBUILD checks === follow the two-peddlers harness pattern exactly.

## Boundaries
- 5d owns the admin UI + import + login wiring; NOTHING in 5c may touch App/XAML.
- One writer: .wt/bg-005c worktree (create it from current main before starting).
- Acceptance: build 0 warnings; coordinator chain (build + test + format verify on changed files) after your push; tests 279 + new all green.

## Standing rules
- Content-exact transcription of docs contracts; if docs are silent or contradictory, make the conservative choice, DOCUMENT it in the report, never ask mid-flight.
- One-writer discipline; explicit-path commits; no history rewrite; build only (no dotnet test) + report SHAs.
