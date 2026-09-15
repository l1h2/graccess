# Extract tools

Read-only tools that list, report or export information from a galaxy: objects, templates, attributes, UDAs, hierarchy.

Rules:

- Never modify a galaxy: no CheckOut, SetValue, AddUDA, Save, CheckIn, import, Deploy, Undeploy or Delete.
- Print to the console, or write files to a run folder under `output\` (see `output\README.md`).
- Name tools `Export...`, `List...` or `Read...`.
