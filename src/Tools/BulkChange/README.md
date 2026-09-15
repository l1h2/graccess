# BulkChange tools

Tools that modify a galaxy. A mistake here can change many objects at once, so every tool in this folder follows this checklist.

## Before writing one

- Develop and test only on a development galaxy (TrainingGalaxy, Galaxy_test or TESTGALAXY).
- Back up a galaxy before the first real run against it, EMGALAXY in particular.
- The check-out calls have not been exercised on this machine yet. Verify them on a test galaxy first (see `docs\GRAccess-Notes.md`), then add a shared check-out helper to `src\Common`.

## Every BulkChange tool must

1. Be a dry run by default: work out every change, write it to `plan.csv` (Object, Attribute, OldValue, NewValue, Action) and change nothing.
2. Change the galaxy only when run with `-Apply`, after the user types the galaxy name to confirm.
3. Read what to change from an explicit input file (e.g. `-Input config\TrainingGalaxy\changes.csv`) and copy that file into the run folder.
4. Skip objects that are checked out by someone else.
5. For each object: CheckOut, change, Save, CheckIn with a comment. Call UndoCheckOut if anything fails.
6. Stop at the first failure (exit code 1), unless the tool offers an explicit `-ContinueOnError`, in which case it finishes and exits with 3 if any object failed.
7. Write `result.csv` with the old and new value of every change.
8. Never Deploy, Undeploy or Delete. Redeploying changed instances is a separate, manual step.

Name tools `Set...`, `Add...`, `Rename...` or `Replace...`, so the exe name shows that the tool writes to a galaxy.

No tools yet.
