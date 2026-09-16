# Evaluate tools

Read-only tools that check a galaxy against rules (naming, required UDAs, security groups, ...) or find where something is used, and report what they find.

Rules:

- Never modify a galaxy: no CheckOut, SetValue, AddUDA, Save, CheckIn, import, Deploy, Undeploy or Delete.
- Report findings on the console when they are meant to be read there. When they are meant to be filtered or kept, write them to `findings.csv` in a run folder under `output\` (see `output\README.md`), one row per finding.
- Exit with `ExitCodes.CompletedWithFindings` (3) when there are findings and `ExitCodes.Success` (0) when there are none, so scripts can react.
- Rules that differ per galaxy go in `config\<Galaxy>\` and are passed with an option such as `-Rules`.
- Name tools `Audit...`, `Check...` or `Validate...`, or `Find...` for tools that find where something is used.

## Tools

- `FindInstanceGraphics`: lists the graphics that include an instance or the objects it contains, and the ViewApps that show them. It looks for embedded symbols, tag references in animations and scripts, and references (Name.Attribute) written in graphic definitions, in checked-in and checked-out versions, and reads the galaxy database with your Windows login. Useful before deleting an instance.
