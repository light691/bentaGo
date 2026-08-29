---
name: verify-bentago
description: Live verification for the BentaGo WPF desktop POS. Drives the real app on Windows through the program's bg001-live.ps1 screen-capture harness with an isolated BENTAGO_DATA_ROOT, and proves behavior with window PNGs plus side-effect checks under artifacts/verification/verify-bentago/. Use it before claiming a UI or boot change works, and for the live lanes in docs/05.
---

# Verify BentaGo

BentaGo is the offline-first peddling POS. The app is a WPF desktop program. This skill drives the real exe the way a user would: launch it, type into it, watch the window, read the files it wrote.

The repo pins .NET SDK 10.0.400 in global.json. The dotnet on PATH is 8.0.419 and cannot load the project. Every command below uses `C:\Users\nicky\.dotnet\dotnet.exe`.

## Launch

Build once.

```
& C:\Users\nicky\.dotnet\dotnet.exe build -c Release C:\BentaGo\BentaGo.sln
```

The exe is `C:\BentaGo\src\BentaGo.App\bin\Release\net10.0-windows\BentaGo.App.exe`.

Drive the app through the wrapper, not by hand.

```
powershell -File C:\BentaGo\.dsh\skills\verify-bentago\scripts\drive.ps1 -Lane <feature>
```

The wrapper sets `BENTAGO_DATA_ROOT` to an isolated directory under the run's evidence folder, launches the exe, waits up to 30 seconds for the main window, and prints `READY` with the window handle. Ready means the boot sequence finished: migration ran and the admin seed completed. Boot failures show a `BentaGo cannot start` message box and the process exits early with code 2.

The wrapper sends the keystrokes you give it, captures the post-keystroke state, then kills the exact process it started. That kill is teardown, not failure. The window captures are the deliverable.

## Doctor

Run this first whenever anything looks off.

```
powershell -File C:\BentaGo\.dsh\skills\verify-bentago\scripts\doctor.ps1
```

It checks the SDK at `C:\Users\nicky\.dotnet`, the built exe, stray `BentaGo.App` processes, and the real data root at `C:\ProgramData\BentaGo`. Warnings are decisions for you, not failures.

## Drive

Every drive starts from an isolated `BENTAGO_DATA_ROOT`. The real instance lives at `C:\ProgramData\BentaGo` and must never be touched by a verification run.

`drive.ps1 -Lane <feature>` runs the program harness (`orchestrate/bentago/scripts/bg001-live.ps1`) against an isolated root and captures the window. Two shots:

- `bg-001-<lane>.png` is the state before the keystrokes.
- `bg-001-<lane>-signedin.png` is the state after the keystrokes.
- The run.log line `(differs-from-login: <bool>)` says whether the state changed.

Keystrokes are a SendKeys sequence. The default is `admin{TAB}admin{ENTER}`. The seeded credentials are username `admin`, password `admin`. SendKeys types into the foreground window, so the drive activates the app window first. An unrelated foreground window can steal the keystrokes; doctor warns about stray processes, and a re-run fixes a stolen drive.

A state change alone is not proof of behavior. Pair the shots with side effects. The wrapper writes `DB_EXISTS`, `DB_BYTES`, and `LOGS_ENTRIES` lines after the harness finishes. A login drive proves the shell when the signed-in shot shows `Signed in. Ready.` and `DB_EXISTS True`.

## Evidence

Every run writes to `C:\BentaGo\artifacts\verification\verify-bentago\<run-id>\`:

- `run.log` with the full transcript
- two PNG shots per drive
- `DB_EXISTS` and `LOGS_ENTRIES` side-effect lines

`artifacts/` is git-ignored, so evidence stays local. Never delete it during cleanup.

## Cleanup

The harness kills the process it started. The wrapper removes the scratch data root (the run folder's `scratch/`) unless you pass `-KeepRoot`, or unless you passed `-DataRoot` and own that directory. A broken run still gets its cleanup. If a drive fails, fix the cause and run it again; a stale `BentaGo.App` process is a doctor warning, and you kill only the PID doctor reports.

## Helpers

- `scripts/doctor.ps1`. Invocation above. Read-only.
- `scripts/drive.ps1`. Invocation above. Params: `-Lane` (required, used in shot names), `-Keystrokes` (SendKeys sequence, default admin login), `-KeepRoot` (keep the scratch data root for inspection), `-DataRoot` (use a specific existing data root; never deleted by the wrapper).

Run the feature-map entry that matches what you changed before claiming it works. Start with one feature. The map exists so later runs cover the rest.

## Feature map

- [features/README.md](features/README.md) — index, baseline, conventions, proof standards.
- [Login happy path](features/login-happy-path.md)
- [Login rejected](features/login-rejected.md)
- [Shell status bar](features/shell-status-bar.md)
- [Data boot](features/data-boot.md)
