# BentaGo standing orders (paste verbatim into every spawn and every resume)

1. Repo root C:\BentaGo. Default branch main. Read docs/00 through docs/05 and docs/decisions/0001 before editing. docs/05 is the program source of truth. docs/04 defines package boundaries.
2. Work only inside your assigned unit boundary and file ownership. One writer per branch and per worktree. Use a git worktree for every writer when two workers run concurrently. Never edit files owned by another unit.
3. Locked stack. .NET 10 pinned by global.json (10.0.400), WPF, MVVM, SQLite, EF Core, keyboard-HID scanner abstraction, ESC/POS printer abstraction, CSV exchange, Windows x64, Velopack. No cloud sync, no web API, no direct main-POS database integration, no unapproved third-party UI frameworks.
4. The system dotnet is SDK 8 and must never be used. Invoke C:\Users\nicky\.dotnet\dotnet.exe explicitly or prepend C:\Users\nicky\.dotnet to PATH in every command that runs dotnet.
5. Money is integer minor units (Philippine centavos). Storage timestamps UTC. UI displays Asia/Manila. Quantities integer unless a decision record says otherwise.
6. Inventory movement is append-only source of truth. InventoryBalance is a rebuildable cache. Every movement references its business document and actor. No destructive edits, no hard deletes, no silent corrections.
7. Checkout is one database transaction. Printing happens after commit. A failed print never rolls back or duplicates the sale. Stable idempotency keys on business submissions.
8. Verification bar. Behavior changes need unit or integration tests that run and pass. Run `dotnet format --verify-no-changes`, `dotnet build -c Release`, and `dotnet test -c Release` before the push-facing commit. Evidence per docs/00 section 7 and the handoff template in docs/04. Screenshot for each material WPF screen change. "It compiles" is not evidence for accounting, inventory, import, printer, or reconciliation work.
9. One owner for BentaGo.Core, BentaGo.Application, EF migrations, shared WPF resources, and package version files, at one time.
10. No rebase, no force-push, no gh PR creation against another unit's branch. The coordinator rebases and the operator merges. Do not merge or alter another agent's unreviewed work.
11. Migrations are sequential. One owner creates or changes EF migrations at a time, always backed up before applying.
12. High-risk work (ledger semantics, money, checkout, idempotency, imports, auth, authz, migrations, restore, reprint, close/reopen) gets an independent reviewer pass before merge-ready, per docs/00 section 8 and docs/05 verdict mechanics.
13. Interaction-changing units stop at merge-ready for operator review. Screenshots to artifacts/review/bg-XXX-<scenario>.png, video where the plan names one. Operator merges.
14. Keep generated databases, secrets, and customer data out of Git. Evidence goes to artifacts/verification/<unit>/<head-sha>/ and review media to artifacts/review/.
15. Run the installed cleanup checks before each commit. Run no-comments before review (deslop and control skills live in cursor-team-kit and are not installed here; note gaps in the report instead).
16. Triage automated review comments as fix, dismiss, or ask. Record the reason.
17. Timebox. On expiry, stop, commit what works, and report partial findings. Never run past the timebox to make coverage look better.
18. Visible progress rule. A builder unit commits after each phase milestone and writes files as it goes, not only at the end. A silent worker (no writes, no commits) is treated as stuck and replaced. Do not spend most of the timebox reading; skim the docs sections your brief names, read the rest only when a step needs it.
19. Slice-on-stall rule (pilot evidence 2026-08-29). When a worker produces no repo change after 25 minutes, replace it and slice the unit smaller. Never re-send the same scope. Builder briefs must inline every requirement the worker needs; reading the docs is for verification context only. A builder brief covers at most two commit milestones.
18. Ambiguity resolution order: docs/05, docs/00, docs/01, docs/02, docs/03, docs/04, docs/decisions, an earlier coordinator note. Never invent business rules. A brief with an unresolved product rule is refused and reported, not guessed.
19. Nothing outside your unit becomes a change. New findings become follow-ups in the report.
20. Report shape per the brief: status, branch, head SHA, verdict, what you actually ran, deviations, forbidden-field violations, suggested follow-ups.
20. Builders verify by build only. The full verification chain (format, test, run) is coordinator-owned and runs at the drain; builders end their slice at a green build plus a commit.
21. Never send mid-flight corrections to a running builder. A correction arrives as a new turn and derails the child (b attempt 1: one wrongful deletion then 70 min of nothing). Stand the child down and respawn with the corrected brief.

## Environment fact (2026-08-29)
- Coordinator session + WORKFLOW agents with explicit model override (provider opencode-go, model deepseek-v4-flash-vision-exp) are VISION-CAPABLE (read_image works). Plain subagent sessions run deepseek-v4-flash WITHOUT image input - route vision work through workflow agents.
- Lane briefs must require visual evidence (screenshots/frames read via read_image) wherever UI behavior is claimed; "no image model" is no longer an excuse.
- The BG-004 PROVISIONAL brand accent was set when vision was unavailable; finalize it from the brand reference image during the bg-004 UX round.

## Model routing (operator directive 2026-08-29, matches ~/.dsh/AGENTS.md)
- **Default = `deepseek-v4-flash-vision-exp`** (provider opencode-go-2) for everything: verification, tests, analysis, debugging, fast work, coordinators, and ALL lane/test/evidence runs. Subagents default to deepseek (preset `standard-vision`).
- **`glm-5.3-flash` = UI passes ONLY.** It is slow; use it exclusively for UI design/implementation and UI passes: finding UI bugs, UI polish/alignment work, generating/redesigning UI. It may run `dotnet build` for its own compile check as part of a UI pass, but NEVER the test/lane/verification chain.
- **Verification always stays on deepseek-v4-flash-vision-exp** — including verifying UI work generated by glm (pixel checks, screenshot audits, running the suite, lane verdicts). A glm pass ships its UI changes; the coordinator (deepseek) runs the full verification chain afterwards.
- If a brief mixes UI work and verification, split it: the glm worker does UI + report; the verification runs on deepseek afterwards, never inside the glm worker.
