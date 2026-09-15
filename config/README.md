# config

Non-secret inputs that tools read: object lists, attribute mappings, rule sets.

- Put galaxy-specific files in a folder per galaxy, e.g. `config\TrainingGalaxy\objects.csv`.
- Tools only read files passed to them explicitly, e.g. `-Input config\TrainingGalaxy\objects.csv`.
- Use CSV for lists and mappings, and XML for structured rules. Both can be read without extra libraries.
- Files named `*.local.*` are specific to one machine and are not shared (ignored by `.gitignore`).
- Never store passwords or other credentials here.
