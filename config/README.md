# config

Settings and inputs for the tools. Never store passwords or other credentials here: galaxy passwords are saved in Windows Credential Manager with `.\bin\SaveGalaxyCredential.exe`.

## defaults.ini

The one file tools read automatically. It sets defaults for the standard options `Galaxy` and `Node`, so they do not have to be typed every time. An option given on the command line always wins. Tools find it from their exe (`bin\..\config\defaults.ini`), so it works from any current folder.

## Inputs for tools

Non-secret inputs such as object lists, attribute mappings and rule sets.

- Put galaxy-specific files in a folder per galaxy, e.g. `config\TrainingGalaxy\objects.csv`.
- Tools read these files only when they are passed explicitly, e.g. `-Input config\TrainingGalaxy\objects.csv`.
- Use CSV for lists and mappings, and XML for structured rules. Both can be read without extra libraries.
- Files named `*.local.*` are specific to one machine and are not shared (ignored by `.gitignore`).
