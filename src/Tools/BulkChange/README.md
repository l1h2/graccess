# BulkChange tools

Tools that modify a galaxy. A mistake here can change many objects at once, so every tool in this folder follows this checklist.

## Before writing one

- Develop and test only on a development galaxy (TrainingGalaxy, Galaxy_test or TESTGALAXY).
- Back up a galaxy before the first real run against it, EMGALAXY in particular.
- Use `ObjectEdits` in `src\Common` to check out, save, check in and undo check-outs. The GRAccess behaviour behind it is in `docs\GRAccess-Notes.md`.

## Every BulkChange tool must

1. Be a dry run by default: work out every change, write it to `plan.csv` and change nothing.
2. Change the galaxy only when run with `-Apply`, after the user types the galaxy name to confirm.
3. Read what to change from an explicit input file and copy that file into the run folder.
4. Refuse objects that are checked out, by anyone.
5. For each object: CheckOut, change, Save, CheckIn with a comment. Undo the check-out if anything fails.
6. Stop at the first failure (exit code 1), unless the tool offers an explicit `-ContinueOnError`, in which case it finishes and exits with 3 if any object failed.
7. Write `result.csv` with what happened to every input row, including the previous definition or value of anything it changed.
8. Never Deploy, Undeploy or Delete. Redeploying changed instances is a separate, manual step.

Name tools `Set...`, `Add...`, `Rename...` or `Replace...`, so the exe name shows that the tool writes to a galaxy.

## Tools

- `AddInstances`: creates instances (with their contained objects) from templates listed in a CSV file, assigns them to an area and gets every object with I/O onto the requested scan group. Objects that do not take the scan group from the area are assigned with the IDE's I/O assignment call (undocumented, see `docs\GRAccess-Notes.md`), and whatever still is not on it is listed for the IDE. Where it differs from the checklist:
  - Rule 5 does not apply: creating an instance and assigning its area and I/O are immediate and need no check-out. Rule 4 applies to the template, which must be checked in; a checked-out area or device is only noted, since neither is changed.
  - A row whose area or I/O assignment failed is not undone, because rule 8 forbids deleting. The failure is recorded in `result.csv` and `objects.csv`.
  - `result.csv` has no previous values (the objects are new); `objects.csv` records each created object's area, scan group, how it got it, and what it was on after the area was assigned.
- `AddTemplateAttributes`: adds UDAs to templates from a CSV file (or updates them with `-o`) and checks that the change reached every derived template and instance. Descriptions, engineering units, Boolean labels and I/O settings are created the same way the IDE creates them, and locked.
