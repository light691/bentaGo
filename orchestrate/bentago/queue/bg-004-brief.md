# BG-004 — WPF shell and design system

Lane-shaped brief (docs/05 lines 233-285). Depends on BG-001 merged + BG-002 contracts (consume after BG-002 merges). REVIEW-GATED (interaction-changing) → architect phase with 2 candidates, stop at merge-ready for operator click.

## Wait, what
The six reference screens (designs/bentago-wpf-mvp-screen-set.png) as a real WPF shell: resources, shell views, shell view models, navigation, commands, dialog service, validation templates, keyboard help. No business logic in code-behind. Placeholder or stable contract data from BG-002 (ScenarioIdSource scenario as the data source for read-only screens is ideal).

## Screens (6)
1. Login (already exists from BG-001 — restyle into the system).
2. Peddler home / dashboard.
3. Sales (checkout entry).
4. Payment (tender + split + credit).
5. Stock (loads, adjustments, count).
6. Reconciliation (trip close; cash count + variance).

## Contracts
- Shortcuts per docs/03-ui-ux-keyboard-shortcuts-philippines.md: F12 = checkout, F10 = end trip, F1 = help, Alt = navigation, Esc = close dialog. Image labels are visual reference only.
- MVVM via the existing ObservableObjectFactory/ViewModelBase/RelayCommand (BG-001 shell).
- No business logic in code-behind; dialogs via a dialog service returning results (testable).
- Two target resolutions: 1366×768 and 1920×1080; 125% scaling; Windows high contrast.

## Evidence
- Unit: `tests/BentaGo.Tests/App/` — navigation, command enablement, focus-restoration requests, shortcut conflicts, validation state, dialog results, view-model disposal; `dotnet test -c Release --filter AppShell`.
- Live L1-L10 EXACTLY per docs/05 via Computer Use (files `bg-004-*.png`): login at both resolutions without scrolling; Alt navigation focus lands on primary control; F1 help lists every documented shortcut with no conflicts; tab order logical + visible; Esc closes only top dialog + restores focus; validation color+text identifies field and fix; six-screen navigation never duplicates views or retains stale dialogs; high contrast keeps focus/status/actions distinguishable; 125% scaling clips nothing.
- Perf: baseline BG-001 shell navigation first; P95 navigation ≤ 150 ms; managed memory +≤ 20 MB after 500 changes.

## Gate
- REQUIRED: screenshots for all six screens @1366×768 → `artifacts/review/bg-004-<screen>.png`; 30-60 s keyboard-only navigation+focus video → `artifacts/review/bg-004-navigation.mp4` (ffmpeg gdigrab). STOP at merge-ready; wait for operator click.
- Audit (accessibility, layout, navigation, code-boundary verdicts at head SHA) → rebase onto main, prove patch unchanged → operator (or delegated coordinator) merges on click.
