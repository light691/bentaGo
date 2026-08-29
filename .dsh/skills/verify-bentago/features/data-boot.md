# Data boot

Data boot covers the app's first-run boot sequence: migrate the SQLite database, seed the admin key/value rows, and log startup. It is the base bootstrap for every other feature.

## Sub-features

- `db-fresh` a fresh data root gets `data/bentago.db` created on first boot.
- `db-idempotent` a second boot on the same data root completes with no boot error.
- `db-logs` a fresh data root gets log entries under `logs/`.
- `db-login` a successful boot reaches the login form (the boot contract gate).

## How to get to it (user POV)

- Launch the app. Boot happens before the login form appears and is invisible to the user except as a clean start or the `BentaGo cannot start` error box.

## Driving it with drive.ps1

Preconditions:

- Doctor is clean.
- The app is built at the baseline exe path.
- The run uses its own isolated `BENTAGO_DATA_ROOT`.

- **Fresh boot.** Run `powershell -File C:\BentaGo\.dsh\skills\verify-bentago\scripts\drive.ps1 -Lane data-boot -Keystrokes ""`. The run.log prints `READY handle=<n>`, `DB_EXISTS True`, `DB_BYTES` greater than 0, and `LOGS_ENTRIES` greater than 0. The shot `bg-001-data-boot.png` shows the login form.
- **Second boot on the same root.** Run again with `-Lane data-boot-2 -Keystrokes "" -KeepRoot -DataRoot C:\BentaGo\artifacts\verification\verify-bentago\<run1>\scratch\data-root` in a second run. The second run.log prints `READY` with no `EXITED early` line and `DB_EXISTS True`.
- **Boot gate.** Neither run.log contains `BentaGo cannot start` and neither run exits early with code 2.

## Gotchas

- `-Keystrokes ""` skips auto-login. Omitting the parameter would attempt login and blur the boot evidence.
- The `-DataRoot` directory is owned by the caller. The wrapper never deletes it. Remove it by hand after the second boot if you kept it only for the two-boot proof.
- Capture the first run's evidence path before the second run. The second run uses a fresh evidence folder unless you pass the same `-DataRoot`.
- A `EXITED early code=2` line means the boot contract failed (data path, migration, or seed). Stop and debug before driving any other feature.
- The corrupt-db and read-only-data-root lanes run through the foundation subcommands, not this UI path. Do not claim them from a UI drive.
