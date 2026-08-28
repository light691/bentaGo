# Open gates

One row per human gate: question, options, default on no answer, status. A gate that is closed records the answer and the decision record that holds it.

| Gate | Question | Options | Default | Status |
|---|---|---|---|---|
| Gate 0 | All 11 client decisions from docs/00 section 3 | Per decisions table | Plan defaults | CLOSED, answered explicitly. Record: docs/decisions/0001-mvp-operating-policy.md |
| Remote | Name the git remote destination (e.g. GitHub repo) so branches become PRs | GitHub org/repo, else none | No remote. PRs are local branches; operator merges locally; no PR links in reports | CLOSED (operator decision 2026-08-29). Repo live: private, default branch main; PR lifecycle operational |
| Hardware | Which devices to build against | Standard PH market picks vs client-specified | Zebra DS2208 scanner, Epson TM-T82 80mm ESC/POS printer. See docs/hardware-profile.md | CLOSED-standard selected (operator 2026-08-29). Client confirmation pending; real devices may change. Real-device verification still gates BG-007 release lanes |
| Client CSV | Product/stock-load fixtures | Client real exports vs coordinator v1 | v1 coordinator fixtures in samples/ (UTF-8, versioned contract) | CLOSED-v1 (operator 2026-08-29). Client real exports replace them later and remain a release gate |
| Computer Use | UI live lanes need a control path for WPF | GUI harness on this machine vs blocked lanes | Blocked lanes fail closed with a documented deviation; screen-capture harness is the replacement | OPEN. Harness at orchestrate/bentago/scripts/bg001-live.ps1. Video lane blocked (no recorder); screenshot-sequence adaptation |
| Client real CSV samples | Real exports before release | From client | v1 fixtures slide in cleanly via versioned contract | OPEN. Release gate only |


