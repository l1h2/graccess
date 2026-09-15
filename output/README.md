# output

Files that tools write when they run. The build never writes here, and neither `.\build.ps1` nor `.\build.ps1 -Clean` deletes anything here.

Each run gets its own folder, which is never reused:

```
output\<ToolName>\<yyyyMMdd-HHmmss>_<Galaxy>\
```

For example `output\ExportTemplates\20260915-181500_TrainingGalaxy\templates.csv`.

- Tools print the full path of the run folder they wrote to.
- CSV files have a header row and are saved as UTF-8 with BOM, so Excel shows them correctly.
- BulkChange runs keep `plan.csv` and `result.csv` here as the record of what changed.
- Output can contain galaxy design and security details. Keep it on this machine and delete old runs by hand.
