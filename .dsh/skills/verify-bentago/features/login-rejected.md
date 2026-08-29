# Login rejected

Login rejected covers wrong credentials. The app stays on the login form, shows the error copy, and clears the error when the next attempt starts.

## Sub-features

- `lj-reject` wrong credentials show `Sign in failed. Check your username and password.`
- `lj-stay` a rejected attempt keeps the login form visible without `Signed in. Ready.`
- `lj-retry-clear` a new attempt clears the previous error before it runs.

## How to get to it (user POV)

- Launch the app, type a wrong username and password, press Enter.

## Driving it with drive.ps1

Preconditions:

- Doctor is clean.
- The app is built at the baseline exe path.
- The run uses its own isolated `BENTAGO_DATA_ROOT`.

- **Boot to login.** Run `powershell -File C:\BentaGo\.dsh\skills\verify-bentago\scripts\drive.ps1 -Lane login-rejected -Keystrokes "wrong{TAB}wrong{ENTER}"`. The run.log prints `READY handle=<n>`.
- **Reject.** The post-keystroke shot `bg-001-login-rejected-signedin.png` shows the red error line `Sign in failed. Check your username and password.` under the password box.
- **Confirm no shell.** The same shot contains no `Signed in. Ready.` text.
- **Confirm state change.** The run.log line `(differs-from-login: True)` is expected here. The error text is the change.

## Gotchas

- Reading the error demands eye proximity, not OCR. Read the shot, not a text extraction.
- A `differs-from-login: False` on this lane means the keystrokes went somewhere else. Re-run.
- The error line renders with a min height, so an empty error area and a visible error can look similar at small sizes. Check the color (`#B3261E` red) and the exact copy.
- A sign-in that succeeds instead of rejecting means the seed changed or the keystrokes were `admin`. Re-run with the wrong-credentials keystrokes.
