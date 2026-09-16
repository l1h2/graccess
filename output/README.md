# output

Files that tools write when they run. The build never writes here, and neither `.\build.ps1` nor `.\build.ps1 -Clean` deletes anything here.

Each run gets its own folder, which is never reused:

```
output\<ToolName>\<yyyyMMdd-HHmmss>_<Galaxy>[_DRYRUN|_APPLY]\
```

For example `output\ExportTemplateAttributes\20260915-181500_EMGALAXY\attributes.csv`.

- BulkChange tools add `_DRYRUN` or `_APPLY` to the folder name, so a dry run is never mistaken for a change.
- When two runs of a tool start in the same second, the later folder gets `_2` (then `_3`, and so on) at the end.
- Tools print the full path of the run folder they wrote to.
- CSV files have a header row and are saved as UTF-8 with BOM, so Excel shows them correctly.
- BulkChange runs keep a copy of the input file here as the record of what changed, plus:
  - `plan.csv`: what would be done with each row. Written by dry runs, and by `-Apply` runs that found errors or nothing to change.
  - `result.csv`: what happened to each row. Written by `-Apply` runs after the galaxy name is confirmed.
  - `propagation.csv` (AddTemplateAttributes): whether every derived template and instance received the change. Written when a changed template has any.
- Output can contain galaxy design and security details. Keep it on this machine and delete old runs by hand.
