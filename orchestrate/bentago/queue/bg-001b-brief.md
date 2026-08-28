# Brief: BG-001b slice (WPF login shell) — READY TO SPAWN after bg-001a lands

Worker spawn conditions: bg-001a done and verified (building, tests pass, foundation verify receipt). Same branch bg-001, single writer at a time. This brief is inlined (proven protocol): instant start, file at a time, commit per phase.

EXECUTION PROTOCOL (mandatory)
1. First action: git -C C:\BentaGo switch bg-001; git -C C:\BentaGo status --short; git -C C:\BentaGo log --oneline -3
2. Files one at a time via write tool. No planning between files. No document reading (everything is below).
3. Commit after each phase. If something fails, fix minimal, or report verbatim and stop.
4. dotnet: $env:Path = "C:\Users\nicky\.dotnet;$env:Path" before every dotnet command. System dotnet 8 is FORBIDDEN.

CONTEXT
- Repo C:\BentaGo, branch bg-001. global.json pins 10.0.400 (do not edit). Never touch main. Never edit docs/, orchestrate/, README.md, samples/, artifacts/.
- Existing from bg-001a: six projects; Infrastructure has BentaGoHost.Build (DI + logging + DataPathProvider + DbContext + IPasswordHasher BCryptPasswordHasher + IAdminSeeder AdminSeeder + IBackupService + IRestoreService); AppSettingKeyValue table stores seed rows with keys "seed.admin.username" (value "admin") and "seed.admin.passwordHash" (BCrypt hash of "admin"). App project currently shows a placeholder text; replace it with the login shell below.
- Gate 0 (docs/decisions/0001): this shell does NOT implement roles/discounts/etc. Authenticate only; role UI is BG-004/005.

PHASE 1 (commit "bg001b-1 mvvm base")
1. src/BentaGo.App/Mvvm/ViewModelBase.cs: INotifyPropertyChanged base with SetProperty, PropertyChanged event.
2. src/BentaGo.App/Mvvm/RelayCommand.cs: ICommand implementation with Action<object?>/Predicate<object?>; CanExecuteChanged via CommandManager.RequerySuggested.
3. src/BentaGo.App/Mvvm/ObservableObjectFactory.cs: minimal, registers view models in a ServiceCollection (constructor injection support) with a static Build() returning IServiceProvider from BentaGoHost with the App-level services added.
PHASE 2 (commit "bg001b-2 login vm")
4. src/BentaGo.App/Features/Login/LoginViewModel.cs: properties Username, Password (SecureString not required; plain string property acceptable for MVP shell but never persisted), ErrorMessage (string?, set on failure, cleared on retry), IsBusy. Commands: LoginCommand (async): builds host via AppServices, asks a ILoginService (see 5) with username/password; on success raises event Authenticated(username); on failure sets ErrorMessage "Sign in failed. Check your username and password." (safe message, no account-existence leak). Command CanExecute when both fields non-empty and not busy.
5. src/BentaGo.Application/Contracts/ILoginService.cs (contract project): Task<bool> AuthenticateAsync(string username, string password, CancellationToken ct). Infrastructure: src/BentaGo.Infrastructure/Security/LoginService.cs: reads seed rows (admin username + hash) via DbContext, Verify via IPasswordHasher. NOT a real user system (BG-005 owns users/roles); document with one comment "// placeholder user store; BG-005 replaces".
PHASE 3 (commit "bg001b-3 shell")
6. src/BentaGo.App/Features/Login/LoginView.xaml: centered card: app name "BentaGo", Username box, Password box (PasswordBox), Log in button, error text, status bar bottom with database/version text. Focused username on load (IsKeyboardFocusWithin / FocusManager InitialFocus). Enter on password triggers LoginCommand.
7. src/BentaGo.App/MainViewModel.cs: wraps LoginViewModel; property StatusText; on Authenticated sets StatusText "Ready" and property IsAuthenticated true; MainWindow swaps content between LoginView and a simple WelcomePanel (text "Signed in. Ready." + version label).
8. src/BentaGo.App/MainWindow.xaml + .cs: Width 1366 Height 768, MinWidth 1024 MinHeight 700, Title BentaGo, grid host for the current view; version label from Assembly.GetEntryAssembly version (1.0.0); no business logic in code-behind (only view wiring). F1 shows a simple dialog listing: F1 Help, Enter submit, Tab move focus, Esc close dialog (placeholder help).
9. Update App.xaml/App.xaml.cs: build host on start (BentaGoHost.Build), ShowDialogless startup, handle unhandled exceptions by logging to host log (Infrastructure logger) and showing a owned error window with the message.
PHASE 4 (commit "bg001b-4 tests")
10. tests/BentaGo.Tests/Foundation/LoginViewModelTests.cs: facts: empty fields disable command; invalid credentials set generic ErrorMessage and no Authenticated event; valid credentials (temp data root, seed admin/admin) authenticate and StatusText becomes "Ready"; password never appears in ErrorMessage. Uses BentaGoHost.BuildForTests(tempRoot) + seed, real BCrypt. Namespace BentaGo.Tests.Foundation so --filter Foundation matches.
11. Run $env:Path = "C:\Users\nicky\.dotnet;$env:Path"; dotnet format --verify-no-changes; dotnet build -c Release; dotnet test -c Release --filter Foundation. Fix all failures. Commit.

FORBIDDEN
- No roles, permissions, navigation beyond the two views, no design-system resources (BG-004), no scanner/printer code, no domain entities, no extra NuGet packages, no UI launch (coordinator screenshots later).
- No commits on main. No push/rebase/force.

TIMEBOX 90 minutes. On expiry commit what is green.

REPORT: STATUS / HEAD SHA / PHASES with SHAs / BUILD-TEST one-liners / DEVIATIONS / UNVERIFIED / FOLLOW-UPS.
