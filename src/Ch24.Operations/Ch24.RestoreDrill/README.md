# Ch24.RestoreDrill: restore the data, not only the binary

A timed drill on temporary storage. The durable stores of the Contoso service live in one SQLite file in WAL mode:

- conversation metadata (no transcripts);
- approvals;
- delivery records (the authorized-actions chapter's `DeliveryWorker` table);
- workflow state;
- the configuration identity (model, prompt and skill hashes, index version, embedding model).

The simulated payment provider (`Ch08.UncertainOutcome.FakePaymentProvider`) keeps its own journal, which the incident does not touch.

| Time | Event |
|---|---|
| 09:00-09:15 | op1-op4 paid through `DeliveryWorker` (op3: accepted, response lost; op4: request lost); op5 (450.00 claimed, 50.00 approved) held as Pending; workflow W-5 checkpointed |
| 09:18 | `File.Copy` of the live file (the reproduction) |
| 09:20 | online backup (`SqliteConnection.BackupDatabase`) with a manifest: newest write, schema, identity digest, row counts, SHA-256, SQLite version |
| 09:25-09:30 | op6 paid; op7 accepted, response lost |
| 09:35 | incident: the live store is deleted; the RTO clock starts |

Then the binary is checked against the backup before any row is read: schema version × embedding model of the index. Binary 2.4.0 (schema 1-2) and 3.0.0 (another embedding model) are refused, and so is a restore over the live path. Binary 3.1.0 restores into an empty folder of its own and verifies SHA-256, `integrity_check`, schema, identity and counts. Next it reconciles, and finally it promotes the copy:

- Pending op5 is held (its approval does not cover it);
- op3 and op4 go through Ch08's `DeliveryWorker.ResumeAsync`: found by reference, or sent again with the same reference;
- op6 and op7 were paid after the recovery point. They are recorded from the provider's statement and put on the uncertainty list, because their approvals and conversations are gone.

Restoring never undoes a payment.

**Measured:** RTO is wall-clock time from the incident to promotion. RPO is the incident time minus the newest write in the backup (drill clock). The targets (60 s, 30 min) are lab choices.

## Self-test

```bash
dotnet run --project src/Ch24.Operations/Ch24.RestoreDrill                  # the drill and its timeline
dotnet run --project src/Ch24.Operations/Ch24.RestoreDrill -- --self-test   # the drill twice, RESULT n/n
```

Two failures are reproduced (`DEFECT REPRODUCED`):

- `File.Copy` returns, and the copy has every table and none of the deliveries: they were still in `contoso.db-wal`, held open by the running service's connection;
- schema 3 changed the meaning of `approvals.amount` (minor units) without renaming it, so the 2.x reader takes 50.00 EUR for 5000.00 EUR.

The second run starts two reconcilers at once (`Task.Run` + `Barrier`). The provider still holds six payments.

Packages: `Microsoft.Data.Sqlite.Core` and `SQLitePCLRaw.bundle_e_sqlite3` (GA, pinned once in `Directory.Packages.props`). No model, no network. Wall-clock timestamps (`deliveries.updated_at`, the provider's `accepted_at`) make the backup's SHA-256 differ between runs. The rest of the output is fixed, except the RTO.
