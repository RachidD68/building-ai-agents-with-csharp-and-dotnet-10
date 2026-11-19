# Ch08.IdempotentCommit

Approval authorizes an operation; it does not stop it from running twice. This sample commits a reimbursement at most once per business operation, in one SQLite transaction (`OperationStore.Submit`):

1. Claim the key: `INSERT ... ON CONFLICT (tenant, operation_id) DO NOTHING` into `operations`, with the canonical argument digest from `Ch08.ActionPolicy`. If the key is taken, read the winner's row: same digest returns the stored outcome (`Replayed`), another digest is a `Conflict`.
2. Recheck the account inside the transaction: version (`StaleVersion`) and balance (`InsufficientBalance`). A refusal rolls back and leaves no trace.
3. Update the balance and version, store the outcome, and write an `outbox` row for the external payment. Commit.

The transaction is `BEGIN IMMEDIATE` (`BeginTransaction(deferred: false)`), so writers queue on the file lock; the UNIQUE constraint is what decides a race.

## Self-test

- 50 identical submits at once, each on its own connection: one `Committed`, 49 `Replayed`, one state change, one outbox row.
- The same submit after a restart replays the stored outcome; `100.00m` replays the operation committed as `100m`.
- The same operation id with another amount, or from another requester, is a `Conflict`.
- A stale version changes nothing; a new operation id at the current version is a new reimbursement.

```bash
dotnet run --project src/Ch08.AuthorizedActions/Ch08.IdempotentCommit -- --self-test
```

Packages: `Microsoft.Data.Sqlite.Core` with `SQLitePCLRaw.bundle_e_sqlite3` (native SQLite for Windows, Linux and macOS), pinned once in `Directory.Packages.props`. The database is a temporary file, deleted at the end.
