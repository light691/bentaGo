# Open gates

One row per human gate: question, options, default on no answer, status. A gate that is closed records the answer and the decision record that holds it.

| Gate | Question | Options | Default | Status |
|---|---|---|---|---|
| Gate 0 | All 11 client decisions from docs/00 section 3 | Per decisions table | Plan defaults | CLOSED, answered explicitly. Record: docs/decisions/0001-mvp-operating-policy.md |
| Remote | Name the git remote destination for PRs and `gh` | GitHub org/repo, else none yet | None yet. PRs are local branches until named | OPEN. Coordinator: no `gh` until named. Worker branches bg-001..bg-009, coordinator reports merge-ready, operator merges locally |
| Hardware | Confirm scanner model, suffix, printer model, interface, paper width | Real devices before BG-007 | Plan defaults in docs/hardware-profile.md | OPEN. Blocks BG-007 release lanes only |
| Client CSV | Main POS real exports for products and stock loads | Client samples before release | Versioned fixtures + samples/input/ | OPEN. Release gate per appendix C |
| Computer Use | UI live lanes need a control path for WPF | GUI harness on this machine vs blocked lanes | Blocked lanes fail closed until a control path works | OPEN. BG-001 lanes adapted: tools command + operator review |
