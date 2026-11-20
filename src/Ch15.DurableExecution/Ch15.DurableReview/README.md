# Ch15.DurableReview

A claim review that survives a process stop. `prepare`, `review` and `resume` are three separate processes. They share two things on disk under `--store`: a checkpoint folder written by `FileSystemJsonCheckpointStore`, and `app.db` (SQLite: claims, reviewer roles, decisions, and the pointer to each paused review). Seeded from `Ch14.CheckpointedApproval`, whose default demo is a same-process simulation.

```
prepare  claim v1 -> summarize -> [claim-review port]   checkpoint + session + graph version -> disk, exit
review   rita approve -> decisions(claim, version 1, rita, approve, expires +24 h)        no workflow runs
resume   graph version? decision still valid NOW? -> restore checkpoint -> answer the re-emitted request -> decide
```

What `resume` checks before it restores anything, against the records as they are now (never against the checkpoint):

| Check | Outcome when it fails |
|---|---|
| stored graph version = this build's graph version (hash of executors, edges, ports + rules version) | `BLOCKED graph_version_mismatch` |
| a decision exists | `WAITING` (exit 5), nothing resumed |
| the decision covers the claim's current version | `BLOCKED stale_approval` |
| it has not expired (24 h) | `BLOCKED approval_expired` |
| the reviewer still holds `finance-reviewer` | `BLOCKED reviewer_revoked` |
| the paused request is for the decided version | `BLOCKED stale_checkpoint` (run `prepare` again) |

A blocked resume finalizes nothing and leaves the checkpoint as it was. `review` refuses a reviewer without the role, the requester (separation of duties) and a second decision on the same version (first writer wins, in one transaction).

The summarizer's executor state goes into every checkpoint (`OnCheckpointingAsync`) and comes back in the resuming process through `RegisterCheckpointRestoringCallback`. In Workflows 1.28 the callback runs before `OnCheckpointRestoredAsync` (the self-test checks the order). `resume` builds the graph with a chat client that throws if called: the summary is in the checkpoint, so the model step is not replayed.

MAF itself refuses a checkpoint from a graph of another shape: `ResumeStreamingAsync` throws `InvalidDataException` ("The specified checkpoint is not compatible with the workflow associated with this runner."), including when only an executor's type changed. A rule changed inside an executor is invisible to it, which is why the rules version is part of the stored graph version (`--rules next --skip-version-check` reproduces the gap). `FileSystemJsonCheckpointStore` admits one process per folder: a second one gets `InvalidOperationException` ("... is already in use by another process").

## Running

```bash
dotnet run --project src/Ch15.DurableExecution/Ch15.DurableReview -- prepare            # Ollama llama3.2 writes the summary
dotnet run --project src/Ch15.DurableExecution/Ch15.DurableReview -- review CLM-1001 rita approve
dotnet run --project src/Ch15.DurableExecution/Ch15.DurableReview -- resume CLM-1001
dotnet run --project src/Ch15.DurableExecution/Ch15.DurableReview -- --self-test
```

Other verbs: `amend <claim> <amount>` (a new claim version), `revoke <subject>`. Options: `--store <dir>`, `--offline` (scripted summary), `--now <ISO time>`, `--graph next`, `--rules next`, `--skip-version-check`. Exit codes: 0 done, 2 usage, 3 failed, 4 blocked or refused, 5 waiting.

`--now` and `--skip-version-check` exist only to reproduce the failures (an expired decision, a resume across graph versions). A production resume takes the time from the server's clock and has no switch that skips the version check.

The self-test runs every verb as a child process against temp stores (offline) and prints `RESULT n/n`, including three concurrent `review` processes (one decision stored) and two concurrent `resume` processes (one `[APPROVED]`).
