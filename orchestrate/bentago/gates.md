# Open gates

One row per human gate: question, options, default on no answer, status. A gate that is closed records the answer and the decision record that holds it.

| Gate | Question | Options | Default | Status |
|---|---|---|---|---|
| Gate 0 | All 11 client decisions from docs/00 section 3 | Per decisions table | Plan defaults | CLOSED, answered explicitly. Record: docs/decisions/0001-mvp-operating-policy.md. Domain rows superseded in part by docs/decisions/0002-store-pos-pivot.md |
| Remote | Name the git remote destination (e.g. GitHub repo) so branches become PRs | GitHub org/repo, else none | No remote. PRs are local branches; operator merges locally; no PR links in reports | CLOSED (operator decision 2026-08-29). Repo live: private, default branch main; PR lifecycle operational |
| Hardware | Which devices to build against | Standard PH market picks vs client-specified | Zebra DS2208 scanner, Epson TM-T82 80mm ESC/POS printer. See docs/hardware-profile.md | CLOSED-standard selected (operator 2026-08-29). Client confirmation pending; real devices may change. Real-device verification gates the BG-009 pilot |
| Client CSV | Product/stock-load fixtures | Client real exports vs coordinator v1 | v1 coordinator fixtures in samples/ (UTF-8, versioned contract) | CLOSED-v1 (operator 2026-08-29). Client real exports replace them later and remain a release gate. Catalog import carries over to the store POS; stock-load exchange died with the pivot |
| Computer Use | UI live lanes need a control path for WPF | GUI harness on this machine vs blocked lanes | Blocked lanes fail closed with a documented deviation; screen-capture harness is the replacement | OPEN. Harness at orchestrate/bentago/scripts/bg001-live.ps1. Video lane blocked (no recorder); screenshot-sequence adaptation. bg-008-reconciliation.mp4 still unrecorded |
| Client real CSV samples | Real exports before release | From client | v1 fixtures slide in cleanly via versioned contract | OPEN. Release gate only |
| BG-008 perf | Close-calc P95 1073ms vs rule <=1000ms (median 872ms, fold-bound) | Optimize the fold vs accept with documented deviation | Accept documented | OPEN. Surfaced in ledger 1852df1; operator decides before or during BG-P5 |
| Pivot checkpoint | BG-P1a checkpoint b310de3 was lost (branch never pushed; old checkout deleted). Accept restart from Core stage | Restart P1a per docs/05 vs salvage attempt | Restart (no salvage source exists) | OPEN-DEFAULT. Recorded in HANDOFF.md section 3b and ledger |
| Design prototype | BG-P5 derives the design from the operator's prototype (docs/05 line 391), and no prototype exists in the repo. Provide one, or regenerate from HANDOFF section 5's design prompt | Operator-made prototype vs design prompt re-run vs keep old designs' patterns and design in-session | Design prompt re-run, mining the two superseded PNGs for UX patterns worth carrying (Hold Sale, GCash reference capture, denomination cash count) | OPEN. Blocks only BG-P5, not BG-P1..P4 |


