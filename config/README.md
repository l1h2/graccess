# config

Settings and inputs for the tools.

## defaults.ini

Defaults for the standard options `Galaxy` and `Node`, so they do not have to be typed every time. An option given on the command line always wins. Tools find it from their exe (`bin\..\config\defaults.ini`), so it works from any current folder.

## credentials.local.ini

Galaxy logins: one `[GalaxyName]` section per galaxy with `User=` and `Password=`. Copy `credentials.example.ini` to start. Git ignores this file, so it stays on this machine. Tools read it automatically, the same way as `defaults.ini`. Galaxies without security need no section.

## Inputs for tools

Non-secret inputs such as object lists, attribute mappings and rule sets.

- Put galaxy-specific files in a folder per galaxy, e.g. `config\TrainingGalaxy\objects.csv`.
- Tools read these files only when they are passed explicitly, e.g. `-Input config\TrainingGalaxy\objects.csv`.
- Use CSV for lists and mappings, and XML for structured rules. Both can be read without extra libraries.
- Files named `*.local.*` are specific to one machine and are not shared (ignored by `.gitignore`).
