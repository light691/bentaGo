# BentaGo verification map

This directory is the maintained source for verifying the user-facing behavior of BentaGo. Read this index before driving the app, then use the matching feature file as the recipe.

## Baseline preconditions

- The SDK at `C:\Users\nicky\.dotnet` is 10.0.400, the version `global.json` pins. The PATH dotnet (8.0.419) cannot load the repo. Use `C:\Users\nicky\.dotnet\dotnet.exe` for every build.
- Build before driving. Run `& C:\Users\nicky\.dotnet\dotnet.exe build -c Release C:\BentaGo\BentaGo.sln`. The build gate is 0 warnings, 0 errors.
- The exe is `C:\BentaGo\src\BentaGo.App\bin\Release\net10.0-windows\BentaGo.App.exe`.
- Every drive sets `BENTAGO_DATA_ROOT` to an isolated directory. The real instance lives at `C:\ProgramData\BentaGo` and must never be touched by a verification run.
- The seeded credentials are username `admin`, password `admin`. Seeding is idempotent.
- Run `scripts\doctor.ps1` and require a clean result before each drive.
- Never drive an instance that was not started by this verification run.

## Driving conventions

- Start every recipe from the baseline state unless its preconditions say otherwise.
- Drive through `scripts\drive.ps1`. It wraps the program harness `orchestrate/bentago/scripts/bg001-live.ps1`. Do not hand-launch the exe in a verification run.
- Treat every command as literal. Keep names and flags unchanged.
- Keystrokes are WPF SendKeys sequences typed into the foreground window. The drive activates the app window first.
- Restore nothing a drive changed; every drive uses a fresh isolated data root, and the wrapper removes it. Do not remove proof artifacts during cleanup.

## Proof and skip reporting

- Capture the user action and the resulting state, not only the final screen. The pre-keystroke shot and the post-keystroke shot are both evidence.
- UI proof includes the window PNG with the app identity visible and the run.log side-effect lines (`DB_EXISTS`, `LOGS_ENTRIES`, `differs-from-login`).
- A state change alone is insufficient proof. Pair shots with side effects: `DB_EXISTS True` for boot, the `Signed in. Ready.` text for login.
- Record the feature ID and lane used with every artifact. The lane name is the `-Lane` value.
- Report an unreachable path with the attempted command and the unmet precondition.
- Do not report a skipped entry point as verified through a different path.

## Features

- [Login happy path](./login-happy-path.md) covers boot-to-login, admin sign-in, and the signed-in shell.
- [Login rejected](./login-rejected.md) covers wrong credentials, the error copy, and the cleared error on retry.
- [Shell status bar](./shell-status-bar.md) covers status text, version line, F1 help, and window geometry.
- [Data boot](./data-boot.md) covers fresh boot creation of the SQLite database and logs, and a second boot on the same data root.
