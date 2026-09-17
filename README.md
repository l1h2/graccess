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
|-- CHEATSHEET.md          Every tool's commands and options, for quick copying
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
|       |-- Evaluate\      Read-only checks and lookups that report findings
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

Every tool accepts the standard options `-Galaxy`, `-Node`, `-User` and `-Help`. Put names that start with `$` in single quotes. [CHEATSHEET.md](CHEATSHEET.md) has every tool's commands and options for quick copying; what each one does and writes is under [Tools](#tools) below.

Exit codes: `0` success, `1` error, `2` usage error, `3` finished with findings (what that means for each tool is under [Tool details](#tool-details)).

Tools that write files put them in a new run folder, `output\<Tool>\<yyyyMMdd-HHmmss>_<Galaxy>\` (BulkChange tools add `_DRYRUN` or `_APPLY`), and print its path at the end. See `output\README.md`.

## Add a tool

1. Pick the category. Extract and Evaluate tools only read; BulkChange tools write, so read `src\Tools\BulkChange\README.md` first; Setup tools prepare this machine.
2. Copy the reference tool and rename its file:
   ```powershell
   Copy-Item -Recurse src\Tools\Extract\ReadGalaxyProperty src\Tools\Extract\ExportTemplates
   Rename-Item src\Tools\Extract\ExportTemplates\ReadGalaxyProperty.cs ExportTemplates.cs
   ```
3. In the new file, rename the class, set the namespace to `GRAccessTools.<Category>`, update `Usage` and `Options`, and replace the body of `Execute`. Keep `Main` as it is.
4. Run `.\build.ps1`. New tool folders are picked up automatically. A tool that needs assemblies other than GRAccess lists their paths in a `references.txt` in its folder (see `src\Tools\BulkChange\AddInstances\references.txt`).
5. Test on a development galaxy (TrainingGalaxy, Galaxy_test or TESTGALAXY), then add the tool to the list and details below and to `CHEATSHEET.md`.

## Conventions

- C# 5 only: no `$"..."`, `?.`, `nameof` or `=>` members. See `docs\GRAccess-Notes.md`.
- Folder name = class name = exe name: PascalCase verb + noun, unique across all categories.
- Open galaxies with `using (GalaxySession session = ToolRunner.OpenGalaxy(args))` and call `GRAccessException.ThrowIfFailed` after every GRAccess call.
- Tools never reference other tools. Code moves into `src\Common` only when a second tool needs it.
- Passwords go only in `config\credentials.local.ini`: never in commands, source, committed files or output.

## Tools

| Tool | Category | What it does |
|---|---|---|
| AddInstances | BulkChange | Creates instances from a CSV file, assigns their area and gets their I/O onto the requested scan group |
| AddTemplateAttributes | BulkChange | Adds (or updates) UDAs on templates from a CSV file and checks propagation |
| ExportInstanceGraphics | Extract | Writes the saved graphics each instance is placed on to CSV, for an instance or an area |
| ExportInstanceIO | Extract | Writes the I/O paths and item references of an instance, or of an area's instances, to CSV |
| ExportTemplateAttributes | Extract | Writes every attribute of a template, or of all templates in a toolset, to CSV |
| FindInstanceGraphics | Evaluate | Lists the graphics and ViewApps that include an instance |
| ReadGalaxyProperty | Extract | Prints a galaxy's name and version, and one attribute of one object |

### Tool details

**AddInstances**
- Dry run unless `-Apply`, which asks you to type the galaxy name. Writes `plan.csv` (dry run) or `result.csv` and `objects.csv`, plus a copy of the input file.
- CSV columns: `template` (the `$` is optional), `name` (the new tagname; contained objects get `<name>_001`, `<name>_002`, ...), `area`, and `io` as `Device.ScanGroup`, e.g. `BACLite_DDESuiteLink.NAE6_Normal`.
- I/O: an object takes the area's scan group when it can. The rest are assigned with the IDE's own call (undocumented; see `docs\GRAccess-Notes.md`), and whatever still is not on the scan group is listed so you can assign it in the IDE.
- `objects.csv`: instance, object, fullName, area, requestedIo, io, status, ioFrom, ioAfterArea, detail.
- Exit 3: some objects need their I/O assigned in the IDE. Nothing is deployed.

**AddTemplateAttributes**
- Dry run unless `-Apply`, which asks you to type the galaxy name. `-o` updates attributes that already exist; without it they are skipped. Writes `plan.csv` (dry run) or `result.csv` and `propagation.csv`, plus a copy of the input file.
- CSV columns: `template` (the `$` is optional), `name`, `Description` (optional), `IO` (`I`, `O`, `IO` or empty), `dataType` (Boolean, Integer, Float, Double, String, Time, ElapsedTime or InternationalizedString), and `label`: Off/On labels for a Boolean (e.g. `Fail/Pass`), engineering units for an Integer, Float or Double.
- Descriptions, engineering units, Boolean labels and I/O settings are created the way the IDE creates them, and locked.
- `propagation.csv`: template, object, kind, status, problems, note.
- Exit 3: the change did not reach every derived template or instance.

**ExportInstanceGraphics**
- `-i` one instance (tagname or full name), or `-a` every instance in an area and all of its sub-areas, each on its own row.
- `instance-graphics.csv`: instance, graphics, e.g. `L2_ECCP; L3_ECCP_Chillers`. Only symbols embedded in saved graphics count, not tag references or scripts. Empty: the instance's symbols are on no graphic. `No linked graphics`: the instance has no symbols of its own (e.g. a set point control).
- Reads the galaxy database with your Windows login; no galaxy login.

**ExportInstanceIO**
- `-i` one instance (tagname or full name), or `-a` every instance in an area and all of its sub-areas.
- `instance-io.csv`: instance, template, attribute, path (the full I/O path, with `---Auto---` resolved), reference (the item reference the path sends to the server). An empty reference means unmapped, not set, or not a device path; the console counts each reason.

**ExportTemplateAttributes**
- `-t` one template (the `$` is optional), or `-d` a toolset path from the top level with levels separated by `/` or `\` (quote paths with spaces). `-r` with `-d` also exports the toolsets below it.
- `attributes.csv`: path, template, name, description, dataType, ioEnabled, uda.

**FindInstanceGraphics**
- Console only. Searches the instance and the objects it contains: embedded symbols, tag references, and references written in graphic definitions (e.g. scripts), in checked-in and checked-out versions, then the ViewApps that show each graphic.
- Not found: edits not saved in the IDE, tag references typed into InTouch windows, and names built at runtime (e.g. OMI asset navigation).
- Exit 3: graphics include the instance; 0: none do. Reads the galaxy database with your Windows login.

**ReadGalaxyProperty**
- Console only. `-Object` defaults to `$UserDefined`, `-Attribute` to `SecurityGroup`.
