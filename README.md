# GRAccess Tools

Command-line tools that use GRAccess to work with AVEVA System Platform galaxies: extract information, make bulk changes, and evaluate a galaxy against rules.

## Requirements

- AVEVA Application Server with the GRAccess Toolkit (verified on 2020 R2 SP1 P01).
- Nothing else. `build.ps1` uses the .NET Framework 4.8 C# compiler that ships with Windows.

## Layout

```
graccess\
|-- build.ps1              Builds everything into bin\
|-- README.md
|-- .gitignore
|-- src\                   All C# source
|   |-- Common\            GRAccessTools.Common.dll: code shared by every tool
|   |   |-- Galaxy\        Connecting, logging in, finding objects, checking GRAccess results
|   |   `-- Cli\           Argument parsing, the standard Main wrapper, exit codes
|   `-- Tools\             One folder per tool, grouped by what the tool is allowed to do
|       |-- Extract\       Read-only reports and exports
|       |-- Evaluate\      Read-only rule checks that report findings
|       `-- BulkChange\    Tools that modify a galaxy (read its README first)
|-- config\                Non-secret inputs for tools: object lists, mappings, rules
|-- docs\                  GRAccess notes and gotchas
|-- bin\                   Build artifacts, created by build.ps1 (safe to delete)
`-- output\                Files written by tools when they run (never deleted by the build)
```

## Build

```powershell
.\build.ps1          # builds src\Common and every tool into bin\
.\build.ps1 -Clean   # deletes bin\
```

## Run

```powershell
.\bin\ReadGalaxyProperty.exe                                    # lists the galaxies on this node
.\bin\ReadGalaxyProperty.exe -Galaxy TrainingGalaxy
.\bin\ReadGalaxyProperty.exe -Galaxy TrainingGalaxy -Object '$UserDefined' -Attribute CodeBase
.\bin\ReadGalaxyProperty.exe -Galaxy EMGALAXY -User <name>      # prompts for the password
.\bin\ReadGalaxyProperty.exe -Help
```

Every tool accepts the standard options `-Galaxy`, `-Node`, `-User` and `-Help`. Put names that start with `$` in single quotes.

Exit codes: `0` success, `1` error, `2` usage error, `3` finished with findings.

## Add a tool

1. Pick the category. Extract and Evaluate tools only read; BulkChange tools write, so read `src\Tools\BulkChange\README.md` first.
2. Copy the reference tool and rename its file:
   ```powershell
   Copy-Item -Recurse src\Tools\Extract\ReadGalaxyProperty src\Tools\Extract\ExportTemplates
   Rename-Item src\Tools\Extract\ExportTemplates\ReadGalaxyProperty.cs ExportTemplates.cs
   ```
3. In the new file, rename the class, set the namespace to `GRAccessTools.<Category>`, update `Usage` and `Options`, and replace the body of `Execute`. Keep `Main` as it is.
4. Run `.\build.ps1`. New tool folders are picked up automatically.
5. Test on a development galaxy (TrainingGalaxy, Galaxy_test or TESTGALAXY), then add the tool to the list below.

## Conventions

- C# 5 only: no `$"..."`, `?.`, `nameof` or `=>` members. See `docs\GRAccess-Notes.md`.
- Folder name = class name = exe name: PascalCase verb + noun, unique across all categories.
- Open galaxies with `using (GalaxySession session = ToolRunner.OpenGalaxy(args))` and call `GRAccessException.ThrowIfFailed` after every GRAccess call.
- Tools never reference other tools. Code moves into `src\Common` only when a second tool needs it.
- Never put passwords in commands, source, config or output.

## Tools

| Tool | Category | What it does |
|---|---|---|
| ReadGalaxyProperty | Extract | Prints a galaxy's name and version, and one attribute of one object |
