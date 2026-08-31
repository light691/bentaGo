# BentaGo standing orders (paste verbatim into every spawn and every resume)

Rewritten 2026-08-30 during the docs alignment pass + operator's session directives. History: the pre-rewrite register lived on the deleted C:\BentaGo checkout; engineering orders below carry over unchanged where still true. Superseded lines are listed at the bottom.

1. Repo root D:\bentago (the ONLY checkout). Default branch main. Read docs/00 through docs/05 and docs/decisions/0001..0003 before editing. docs/05 is the program source of truth. docs/decisions/ holds the pivot (0002) and BIR readiness (0003) decisions.
2. Work only inside your assigned unit boundary and file ownership. One writer per branch and per worktree (worktrees live under D:\bentago\.wt\<name> when two workers run concurrently). Never edit files owned by another unit.
3. Locked stack. .NET 10 pinned by global.json (10.0.400), WPF, MVVM, SQLite, EF Core, keyboard-HID scanner abstraction, ESC/POS printer abstraction, CSV exchange, Windows x64, Velopack. No cloud sync, no web API, no direct main-POS database integration, no unapproved third-party UI frameworks.
4. The .NET SDK 10.0.400 is installed at %USERPROFILE%\.dotnet\dotnet.exe (no system dotnet). Prepend %USERPROFILE%\.dotnet to PATH in every command that runs dotnet.
5. Money is integer minor units (Philippine centavos). Storage timestamps UTC. UI displays Asia/Manila. Quantities integer unless a decision record says otherwise.
6. Inventory movement is append-only source of truth. InventoryBalance is a rebuildable cache. Every movement references its business document and actor. No destructive edits, no hard deletes, no silent corrections.
7. Checkout is one database transaction. Printing happens after commit. A failed print never rolls back or duplicates the sale. Stable idempotency keys on business submissions.
8. Verification bar. Behavior changes need unit or integration tests that run and pass. Run `dotnet format --verify-no-changes`, `dotnet build -c Release`, and `dotnet test -c Release` before the push-facing commit. Evidence per docs/00 section 7. Screenshot for each material WPF screen change. "It compiles" is not evidence for accounting, inventory, import, printer, or reconciliation work.
9. One owner for BentaGo.Core, BentaGo.Application, EF migrations, shared WPF resources, and package version files, at one time.
10. No rebase, no force-push, no PR creation against another unit's branch. The coordinator rebases and squash-merges. Do not merge or alter another agent's unreviewed work.
11. Migrations are sequential. One owner creates or changes EF migrations at a time, always backed up before applying. Schema reset owner: BG-P1 owns the ONE-baseline-migration reset including Infrastructure (this supersedes the old stage-boundary that excluded Infrastructure from the pivot unit).
12. High-risk work (ledger semantics, money, checkout, idempotency, imports, auth, authz, migrations, restore, reprint, close/reopen) gets an independent reviewer pass before merge-ready, per docs/00 section 8 and docs/05 verdict mechanics.
13. MERGE DELEGATION (operator, this session): the coordinator squash-merges merge-ready PRs automatically WITHOUT waiting for the operator review gate. The coordinator still stops for irreversible actions (force-push to shared branches, deletions, deploys, anything data-destructive). Interaction-changing units still produce a review pack (screenshots to artifacts/review/); the operator reviews AFTER merge, not as a gate.
14. Keep generated databases, secrets, and customer data out of Git. Evidence goes to artifacts/verification/<unit>/<head-sha>/ and review media to artifacts/review/.
15. Triage automated review comments as fix, dismiss, or ask. Record the reason.
16. Timebox. On expiry, stop, commit what works, and report partial findings. Never run past the timebox to make coverage look better.
17. Visible progress rule. A builder unit commits after each phase milestone and writes files as it goes, not only at the end. A silent worker (no writes, no commits) is treated as stuck and replaced. Do not spend most of the timebox reading; skim the docs sections your brief names, read the rest only when a step needs it.
18. Slice-on-stall rule (pilot evidence 2026-08-29). When a worker produces no repo change after 25 minutes, replace it and slice the unit smaller. Never re-send the same scope. Builder briefs must inline every requirement the worker needs; reading the docs is for verification context only. A builder brief covers at most two commit milestones.
19. Ambiguity resolution order: docs/05, docs/decisions, docs/00, docs/01, docs/02, docs/03, docs/04, an earlier coordinator note. Never invent business rules. A brief with an unresolved product rule is refused and reported, not guessed.
20. Nothing outside your unit becomes a change. New findings become follow-ups in the report.
21. Report shape per the brief: status, branch, head SHA, verdict, what you actually ran, deviations, suggested follow-ups.
22. Builders verify by build only. The full verification chain (format, test, run) is coordinator-owned and runs at the drain; builders end their slice at a green build plus a commit.
23. Never send mid-flight corrections to a running builder. A correction arrives as a respawn with a corrected brief.
24. DESIGN HOLD (operator, this session): the operator is producing the BG-P5 design prototype. Do NOT start BG-P5, do not run the design prompt, do not touch visual design. BG-P1..P4 proceed normally.
25. DELEGATION ROUTING (operator, this session): every delegated child runs glm-5.3-flash at reasoning effort high on provider opencode-go (pinned in the DSH profile patch and provider profile). No per-unit model overrides unless the coordinator's brief says so explicitly.

## Superseded lines (kept for history)

- Old line 4 (system dotnet is SDK 8; invoke C:\Users\nicky\.dotnet): machine is gone; SDK 10.0.400 now local at %USERPROFILE%\.dotnet.
- Old model routing section (deepseek-v4-flash-vision-exp default, glm UI-only): superseded by operator order 2026-08-30, all children glm-5.3-flash high (order 25). The deepseek-v4-flash-vision-exp provider entry remains available for the BG-P5 design work when the hold lifts.
- Old line 13 (interaction-changing units stop at merge-ready for operator merge): superseded by merge delegation (order 13).
