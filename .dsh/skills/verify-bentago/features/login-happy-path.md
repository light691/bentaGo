# Login happy path

Login lets a user sign in with the seeded administrator account and reach the signed-in shell. Success replaces the login form with `Signed in. Ready.` and flips the status bar to `Ready`.

## Sub-features

- `lg-boot` the app boots to the login form with username, password, and a `Log in` button.
- `lg-accept` `admin` / `admin` signs in.
- `lg-shell` a successful sign-in shows `Signed in. Ready.` and the status bar reads `Ready`.
- `lg-submit-enter` Enter submits the form.

## How to get to it (user POV)

- Launch the app. The login form is the first screen.
- Type the username, press Tab, type the password, press Enter. Or click `Log in`.

## Driving it with drive.ps1

Preconditions:

- Doctor is clean.
- The app is built at the baseline exe path.
- The run uses its own isolated `BENTAGO_DATA_ROOT`.

- **Boot to login.** Run `powershell -File C:\BentaGo\.dsh\skills\verify-bentago\scripts\drive.ps1 -Lane login-happy`. The run.log prints `READY handle=<n>` within 30 seconds. The shot `bg-001-login-happy.png` shows the BentaGo login form, `Offline · local data` posture in the status bar, and the version text.
- **Sign in.** The drive sends the default keystrokes `admin{TAB}admin{ENTER}`. The post-keystroke shot `bg-001-login-happy-signedin.png` shows `Signed in. Ready.` replacing the form and the status bar reading `Ready`.
- **Confirm side effect.** The run.log prints `DB_EXISTS True` and `DB_BYTES` greater than 0. Both shots and the side-effect lines together are the proof.
- **Confirm state change.** The run.log line `(differs-from-login: True)` says the screen changed after the keystrokes.

## Gotchas

- SendKeys types into the foreground window. An unrelated foreground window steals the keystrokes and the signed-in shot stays on the login form. Close interfering windows and re-run.
- The shot can be larger than the window if the drive falls back to the virtual screen. The evidence is still valid; the window content is present.
- `admin` / `admin` is the only seeded account. A different expected account means the seed changed, not a login bug.
- A login shot with `Signed in. Ready.` but `DB_EXISTS False` means the drive ran against a non-isolated root. Stop and re-run through drive.ps1.
