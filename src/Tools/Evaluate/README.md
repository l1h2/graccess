# Evaluate tools

Read-only tools that check a galaxy against rules (naming, required UDAs, security groups, ...) and report what they find.

Rules:

- Never modify a galaxy: no CheckOut, SetValue, AddUDA, Save, CheckIn, import, Deploy, Undeploy or Delete.
- Write findings to `findings.csv` in a run folder under `output\` (see `output\README.md`), one row per finding.
- Exit with `ExitCodes.CompletedWithFindings` (3) when there are findings and `ExitCodes.Success` (0) when there are none, so scripts can react.
- Rules that differ per galaxy go in `config\<Galaxy>\` and are passed with an option such as `-Rules`.
- Name tools `Audit...`, `Check...` or `Validate...`.

No tools yet.
