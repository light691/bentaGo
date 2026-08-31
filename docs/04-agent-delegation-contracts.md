# BentaGo Agent Delegation Contracts

**Historical record.** The contracts below are the pre-pivot program's work packages, run as BG-001 through BG-008. They are preserved as the record of how the first program was run and are not the current delegation plan. The current program's delegation rules live in `docs/05-pstack-program-execution-plan.md` (standing rules, one-writer and branch rules, standing delegation note) and in `orchestrate/bentago/HANDOFF.md` section 7: one writer per worktree, build and test and format gates on every PR, squash-merge at merge-ready, and operator review for interaction-changing PRs. Do not dispatch new work from the packages below. The body is unchanged.

This document turns the master plan into small, non-overlapping implementation packages. It is intentionally ordered; agents must not leap ahead of their dependencies.

## Global contract for every agent

- Read `docs/00` through `docs/03` before editing.
- Work only inside the assigned package and listed ownership boundary.
- Do not change stack, schema, public contracts, or shared styles without coordinator approval.
- Do not add cloud/API features, direct POS integration, or unapproved third-party UI frameworks.
- Use the project’s WPF/MVVM conventions; no domain logic in code-behind.
- Add tests before declaring behavior complete; use SQLite integration tests for persistence behavior.
- Do not merge or alter another agent’s unreviewed work.
- Report evidence using the template at the bottom.

## Package A — Solution foundation

**Depends on:** Gate 0 and Gate A.  
**Owns:** solution/project setup, package versions, DI bootstrap, configuration, logging, data paths, migrations bootstrap, test infrastructure, backup/restore scaffolding.  
**Does not own:** final domain schema, POS UI, import, printer, reporting.  
**Acceptance:** application starts at 1366×768; creates/migrates database; test admin can log in; backup and restore prove the database contents are retained; CI/local test command passes.

## Package B — Domain, persistence, and ledger

**Depends on:** Package A.  
**Owns:** Core entities/value objects/enums, EF mappings and migrations, movement ledger, balance projection, trip state machine, sale/load/return/void/adjustment/close services, audit records.  
**Does not own:** WPF views, CSV UI, concrete printer code.  
**Acceptance:** full two-peddler service-level test passes; no negative stock without authorized override; reversal records preserve original data; balance can rebuild from movements.

## Package C — CSV exchange and operational setup

**Depends on:** Package B.  
**Owns:** versioned CSV contracts, parser/validator, preview/atomic commit, import history/error output, exports, product/peddler/user/trip/load administration view models and views.  
**Does not own:** modifications to ledger semantics or transaction boundaries.  
**Acceptance:** representative valid/invalid CSV fixtures; all-or-nothing import; leading zeros preserved; export totals reconcile to the seeded ledger.

## Package D — WPF design system and navigation shell

**Depends on:** Package A; consume stable DTOs after Package B.  
**Owns:** native WPF theme/resources, main `Grid`/`DockPanel` layout, navigation, status bar, command/keybinding infrastructure, dialog service, validation templates, responsive 1366×768 layout checks.  
**Reference:** `designs/bentago-wpf-mvp-screen-set.png` and `docs/03`.  
**Does not own:** calculation logic or persistence.  
**Acceptance:** all six reference pages navigate with placeholder data; F1/help and required function keys work; keyboard focus is visible; no critical clipping at 1366×768.

## Package E — POS selling workflow

**Depends on:** Packages B and D.  
**Owns:** scanner input adapter/view-model integration, product search, cart, quantity, allowed discounts, payment dialog, split payment, checkout command/result, print status presentation.  
**Does not own:** low-level printer transport or ledger changes.  
**Acceptance:** keyboard-only sale; scanner input remains focused/restores focus; duplicate submit is idempotent; cash change and split total calculation are correct; failed print shows saved-sale/reprint state.

## Package F — Hardware and correction workflows

**Depends on:** Packages B, D, and E.  
**Owns:** ESC/POS printer adapter, receipt rendering/template, printer/scanner diagnostic screens, reprint, void, return, damage/loss, adjustment views.  
**Does not own:** edits to completed checkout semantics without Package B review.  
**Acceptance:** fake-adapter tests and real-device checklist; failed printer never rolls back/duplicates sale; all corrections create auditable reversals or movements.

## Package G — Reconciliation, reports, and release readiness

**Depends on:** Packages B, C, D, E, F.  
**Owns:** end-trip/cash/count screens, variance display and approvals, report view models/views, CSV and print exports, close/reopen UI, packaging/upgrade checklist, pilot script.  
**Does not own:** invention of new financial rules.  
**Acceptance:** end-to-end pilot scenario reconciles from exports; close lock works; variance is explicit; clean installation and upgrade check completed.

## Integration order

1. Coordinator accepts Package A.
2. Coordinator and independent reviewer accept Package B.
3. Integrate C and D sequentially or in isolated worktrees.
4. Integrate E after B/D contracts are frozen.
5. Integrate F.
6. Integrate G.
7. Run the pilot script and an independent final review.

## Agent handoff template

```text
Package:
Acceptance criteria completed:
Tests and results:
Manual checks and screenshots:
Files/migrations/configuration changed:
Known risks or follow-ups:
Assumptions made:
Suggested reviewer focus:
```
