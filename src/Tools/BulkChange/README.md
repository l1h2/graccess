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

- `AddTemplateAttributes`: adds UDAs to templates from a CSV file (or updates them with `-o`) and checks that the change reached every derived template and instance. Descriptions, engineering units, Boolean labels and I/O settings are created the same way the IDE creates them, and locked.
