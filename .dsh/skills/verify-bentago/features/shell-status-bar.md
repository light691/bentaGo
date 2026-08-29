# Shell status bar

Shell status bar covers the app chrome: the bottom status line, the version text, the F1 help dialog, and the window geometry.

## Sub-features

- `st-status` the status bar reads `BentaGo` while signed out and `Ready` after sign-in.
- `st-version` the status bar right side shows `v1.0.0` style version text.
- `st-help` F1 opens the `BentaGo Help` message box listing the shortcut lines.
- `st-geometry` the window opens at 1366x768, centered, title `BentaGo`, minimum 1024x700.

## How to get to it (user POV)

- Launch the app and look at the bottom status line.
- Press F1 from anywhere in the window.

## Driving it with drive.ps1

Preconditions:

- Doctor is clean.
- The app is built at the baseline exe path.
- The run uses its own isolated `BENTAGO_DATA_ROOT`.

- **Read the chrome.** Run `powershell -File C:\BentaGo\.dsh\skills\verify-bentago\scripts\drive.ps1 -Lane shell-status`. The pre-keystroke shot shows the title `BentaGo`, the bottom bar with `BentaGo` on the left and `v1.0.0` on the right.
- **Post-login chrome.** Run the same lane with default keystrokes and read the signed-in shot. The bottom bar left reads `Ready`.
- **Open help.** Run `powershell -File C:\BentaGo\.dsh\skills\verify-bentago\scripts\drive.ps1 -Lane shell-help -Keystrokes "{F1}"`. The post-keystroke shot `bg-001-shell-help-signedin.png` shows the `BentaGo Help` message box with the shortcut lines `F1  Help`, `Enter  Submit the login form`, `Tab  Move focus`, `Esc  Close dialogs`.
- **Geometry.** The shots show a 1366x768 window. Exact pixel geometry is not evidence; the title and content layout are.

## Gotchas

- The harness kills the app after the shot. The help dialog does not need closing for the run to be clean.
- The version text reads from the assembly version. A version bump changes the expected string.
- The status bar is 28px tall and easy to overlook. Read the bar, not the window center.
- Keystrokes for help use `{F1}` only. A keystrokes value with surrounding quotes is taken literally by SendKeys.
