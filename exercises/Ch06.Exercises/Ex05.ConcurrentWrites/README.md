# Exercise 05 — Concurrent Writes (Challenge)

## Prompt

Two requests load the same conversation at the same version and both save. Build on `Ch06.SessionOwnership`: make the losing save fail with an explicit conflict, then recover so that no turn is lost and none is stored twice. Prove it with a test that really runs the two saves at the same time.

## Hints

- `SqliteSessionStore` already refuses a save whose loaded version is no longer the stored one (`SessionConflictException`). The work is on the caller's side.
- On a conflict, reload the conversation: it now holds the other writer's turn. Run your turn again on that copy and save it. Bound the retries.
- Load both copies before either save starts, and release both saves from one `ManualResetEventSlim`, or the "race" is two saves one after the other.

## Accept

- Two concurrent saves from the same loaded version: one success and one `SessionConflictException`.
- After the retry, every line sent is stored, each once.
- `dotnet run -- --self-test` prints `RESULT 4/4`.

## What this does not prove

A retry runs the model again on the new history, so the second answer can differ from the first. For a turn that triggered an action, retrying the turn is not enough: the action needs its own idempotency key (the authorized-actions chapter).
