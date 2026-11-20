# Contoso v5: durable resume

The Contoso expense assistant at the end of the durable-execution chapter. v4's receipt claim waits for a reviewer, and the wait now survives a process stop.

```
prepare  taxi receipt -> v4 ReceiptIntake -> v2 desk: AwaitingReview (approval_missing)
         -> review workflow runs to its port -> checkpoint + session + graph version on disk -> exit
review   rita approve -> decision bound to the amount of record and the account version   (no workflow runs)
resume   lease (fencing token) -> v2 desk.ResumeAsync (repair, outbox, reconciliation)
         -> graph version check -> decision revalidated NOW -> restore checkpoint
         -> pay step -> v2 desk.ReviewAsync: one commit, one payment
```

What v5 adds to v4, all in the v2 desk's `app.db`: `identity_rights` (the identity system's answer, loaded fresh by every process, so a revoked role stays revoked), `review_decisions`, `review_sessions` and `leases` (`Ch15.ExternalEffects`'s `LeaseStore`). Checkpoints are in `checkpoints/` (`FileSystemJsonCheckpointStore`); the provider's journal is `provider.db` (v2's fake). The graph-version hash, the lease store and the step hooks are the chapter's files, compiled in as links; v4 is a project reference, used as it is.

The pay step runs again after any crash, so it reads the claim first: a claim that already left `AwaitingReview` is reported as it stands (`re-read the claim: already Delivered`), never reviewed or paid twice. A payment a crash left unresolved is settled by v2's own `ResumeAsync` before the workflow resumes. The lease fences the v5 step; the v2 desk's commit is idempotent by operation id, so a worker that slipped past the check would replay, not duplicate.

`resume` blocks, with nothing committed, on `graph_version_mismatch`, `stale_approval` (the amount or the account version changed after the decision), `approval_expired` (24 h) and `reviewer_revoked`.

## What this snapshot does not do

- `prepare` reads the receipt with a scripted reading of the clean taxi receipt unless `--live` is given (`--live` uses the `VisionModel` section, Ollama `llava` by default). Receipts waiting for verification still live in memory, as in v4.
- The checkpoint store is a folder, which admits one process at a time; a shared production store needs the lease and fencing that `Ch15.ExternalEffects` shows. No preview package is used.

## Running

```bash
dotnet run --project contoso/v5-durable-resume -- prepare
dotnet run --project contoso/v5-durable-resume -- review latest rita approve
dotnet run --project contoso/v5-durable-resume -- resume latest
dotnet run --project contoso/v5-durable-resume -- status latest
dotnet run --project contoso/v5-durable-resume -- --self-test
```

Other verbs: `revoke <subject>`, `account-changed <employee>` (another claim committed first). Options: `--store <dir>`, `--now <ISO time>`, `--graph next`, `--lease-seconds <n>`, `--pause-at <point>`. The kill-at-each-point proof is `Ch15.RestartTests`. Simulated effects only.
