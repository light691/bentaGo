BG-P1d: real SqliteAppDataSource + kill the simulated label
Status: queued (launch after P1c merges)
Author: coordinator, 2026-08-30

PREREQS
- P1a (Core), P1b (Application), P1c (schema baseline) merged.
- Read orchestrate/bentago/preferences.md, HANDOFF.md section 8, docs/05
  BG-P1 box.

SCOPE
1. Build SqliteAppDataSource: the real SQLite-backed IAppDataSource that
   serves screens from the live database (fold-derived snapshot like the
   scenario source, but persisted). It replaces
   ScenarioDataSource at src/BentaGo.App/.../ObservableObjectFactory.cs:54
   ("swap this line only" — the comment's promise, finally kept).
2. Delete or archive TwoPeddlersScenario/TripLifecycleScenario remnants if
   any survived P1b (they should not have).
3. Kill the SimulatedLabel: MainViewModel.cs:54 ("Simulated data - BG-002
   scenario"), MainWindow.xaml:149, HelpOverlayView.xaml:71.
4. PeddlerHome screen: delete it and register the placeholder that P5's
   Register-first shell will replace (shell must build and run).
5. Startup path: the app opens the real database, applies the baseline
   migration, shows login, and the Register screen reads real (empty) data.
   Backup-before-migration behavior from BG-003 must still fire.

ACCEPTANCE
- dotnet build -c Release 0/0; full dotnet test green (retargeted tests);
  format clean.
- Manual/headless run: app starts on a fresh DB with no "Simulated data"
   label anywhere (`git grep -i "simulated" -- src` = 0).
- The Stock and Transactions screens read from SQLite (integration test
  with a seeded day).

WORKFLOW
- Worktree D:\bentago\.wt\bgp1d, branch bgp1d from origin/main. Push when
  green; coordinator squash-merges (standing order 13).
COMMIT
BG-P1d: real SqliteAppDataSource; simulated-data path deleted
