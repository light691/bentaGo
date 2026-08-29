# BG-005d-ui — admin screens on the reworked design system (UI; interaction-change gate)

Status: READY (after ui-rework merged 3debaf7/d876ae9). UI generation = glm lane; coordinator (deepseek) verifies everything. This slice STOPS at merge-ready for the operator click (interaction-changing).

## Scope (docs/05 admin views; BG-005a/b/c service surfaces are ready)
1. **User management screen** (Features/Admin/UserManagementView + VM): list users (role, active/deactivated, last lock state), create user (username/password/role), set active/inactive, change password (admin-any or self) — backed by IUserAccountService (+ IActorContext session binding from 5d-core: service rejects mismatched By; the VIEW must pass the session user — wire via the service that already enforces; role gate via RoleExtensions caps, not UI-only). Show only what the current user may do (Admin gates; logged-in user's own row read-only except password).
2. **Catalog import screen** (Features/Admin/CatalogImportView + VM): pick/specify the CSV path (or reuse the last-known path), run preview (IProductImportService.PreviewAsync → rows with derived SKU + per-row errors + summary), confirm import (ImportAsync → Inserted/Updated summary), show error rows. No auto-drag-drop file dialogs beyond a standard OpenFileDialog if the app already has one; keep keyboard-first per the design system.
3. **Admin overview / trips screen** (Features/Admin/AdminOverviewView + VM): per-peddler active trip + route summary + today's sales (from ITripReadService/on-hand read models), read-mostly; admin gets trip visibility across peddlers (the services are peddler-scoped — read-only cross-peddler view needs either new read APIs (small, domain-side, IN THIS SLICE — coordinate with the audit-categories lane via the coordinator: ONLY the read API + its tests, no UI) or default to per-peddler selection. CONSERVATIVE: per-peddler selection UI + peddler picker; note cross-peddler read API as follow-up.)
4. Navigation: admin entries in the rail (visible only for Admin role — role gating uses RoleExtensions; entries use the existing ScreenKeys/pattern; the shell learns role from the auth result/actor).

## Design system (MANDATORY - follow exactly)
- Tokens + styles: src/BentaGo.App/Themes/Theme.xaml (tokens incl Brand.Rail #0A2E4F family, Accent #03873F) + Styles/ScreenStyles.xaml; screens follow the six-screen visual language: eyebrow label + heading, cards with Surface.Card, action bars with parked/enabled states, keyboard-first chips (F-keys), status/error copy conventions, dark navy rail (never light), 240/1326 grid at 1366x768.
- Reference: designs/bentago-wpf-mvp-screen-set.png + the merged review pack (artifacts/review/bg-004-*.png - the CURRENT merged renders).
- Interop: single-window WPF MVVM per existing App; screens route through MainViewModel/NavigationService; keyboard navigation + focus-on-navigate per existing AppShell tests (they must pass unchanged + new admin screens add their own navigation entries WITHOUT breaking the existing 314).

## Deliverables (2 commits, milestone-split, explicit paths; NO dotnet format; build pinned SDK; no test run - coordinator verifies; build-only verification per standing rule)
- Commit 1: Features/Admin VMs + data services glue (IProductImportService/IUserAccountService binding, session pass-through) — buildable without the XAML? (No - commit 1 = VMs and glue, commit 2 = Views + shell entries + style additions.)
- Commit 2: Views (XAML per design system), shell rail entries gated by role, style additions ONLY if they reuse existing tokens (new styles must use existing token names; new TOKENS are NOT allowed without coordinator approval).
- Scope guard: NO new token changes, NO changes to existing six screens' XAML/Theme/ScreenStyles except ADDITIVE styles (prefer reusing). NO docs changes.

## Acceptance (coordinator, after your push)
- Build 0 warnings; 314 + new green; format 0 diagnostics; review pack REGENERATION with admin screens included (new screenshot pack for the operator gate: all six existing + 3 admin screens); the operator review gate = the click before merge. Include a written review-summary for the operator (what each screen does, gates, keyboard-first notes).

## Standing
- Never edit Theme.xaml token values; screen copies follow the CURRENT design language; if something in the design system blocks you (missing style/token), REPORT it instead of inventing one.
- One writer: worktree .wt/bg-005dui (created from current main). Content-exact per design system; conservative documented fallback; never ask mid-flight; report SHAs + interpretation notes.
