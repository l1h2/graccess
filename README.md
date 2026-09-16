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
|   |   |-- Cli\           Argument parsing, the standard Main wrapper, exit codes
|   |   |-- Input\         Reading input files such as CSV
|   |   |-- Output\        Run folders under output\ and CSV files
|   |   `-- Settings\      Reading config\defaults.ini and config\credentials.local.ini
|   `-- Tools\             One folder per tool, grouped by what the tool is allowed to do
|       |-- Extract\       Read-only reports and exports
|       |-- Evaluate\      Read-only rule checks that report findings
|       |-- BulkChange\    Tools that modify a galaxy (read its README first)
|       `-- Setup\         Tools that prepare this machine
|-- config\                defaults.ini (default galaxy), credentials.local.ini (logins) and inputs for tools
|-- docs\                  GRAccess notes and gotchas
|-- bin\                   Build artifacts, created by build.ps1 (safe to delete)
`-- output\                Files written by tools when they run (never deleted by the build)
```

## Build

```powershell
.\build.ps1          # builds src\Common and every tool into bin\
.\build.ps1 -Clean   # deletes bin\
```

## Galaxy settings

Tools connect to the galaxy set in `config\defaults.ini` (currently EMGALAXY) unless you pass `-Galaxy`.

For a galaxy with security enabled, put the login in `config\credentials.local.ini`. Copy `config\credentials.example.ini` to start. The file is ignored by git, so it stays on this machine:

```ini
[EMGALAXY]
User=AVEVAUser1
Password=...
```

How a tool logs in:

- **Galaxy and node:** `-Galaxy` and `-Node`, otherwise `config\defaults.ini`, otherwise this computer for the node.
- **User:** `-User`, otherwise `User` in the galaxy's section of `config\credentials.local.ini`, otherwise none (for galaxies without security).
- **Password:** `Password` from that section when the user matches, otherwise prompted.

## Run

```powershell
.\bin\ReadGalaxyProperty.exe                                     # default galaxy with the login from credentials.local.ini
.\bin\ReadGalaxyProperty.exe -Object '$UserDefined' -Attribute CodeBase
.\bin\ReadGalaxyProperty.exe -Galaxy TrainingGalaxy              # a galaxy without security
.\bin\ReadGalaxyProperty.exe -Help
```

Every tool accepts the standard options `-Galaxy`, `-Node`, `-User` and `-Help`. Put names that start with `$` in single quotes.

Exit codes: `0` success, `1` error, `2` usage error, `3` finished with findings.

## Add a tool

1. Pick the category. Extract and Evaluate tools only read; BulkChange tools write, so read `src\Tools\BulkChange\README.md` first; Setup tools prepare this machine.
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
- Passwords go only in `config\credentials.local.ini`: never in commands, source, committed files or output.

## Tools

| Tool | Category | What it does |
|---|---|---|
| AddTemplateAttributes | BulkChange | Adds (or with `-o` updates) UDAs on templates from a CSV file and checks propagation. Dry run unless `-Apply`: `-f config\AddTemplateAttributes-test.csv` |
| ExportInstanceIO | Extract | Writes the full I/O paths of an instance, or of every instance in an area and its sub-areas, to CSV: `-i LSC3_PumpVFDControl_CHWR.SetPointControl`, `-a LifeSci3`; add `-u` for only the points missing from their scan group's ItemList |
| ExportTemplateAttributes | Extract | Writes every attribute of a template, or of all templates in a toolset, to CSV: `-t Pump`, `-d Radix/Equipment/Pump`, `-d Radix/Equipment -r` |
| ReadGalaxyProperty | Extract | Prints a galaxy's name and version, and one attribute of one object |
