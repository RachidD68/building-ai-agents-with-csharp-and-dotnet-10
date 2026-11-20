# Checkpointed Approval

The checkpoint mechanism behind a paused human review. The workflow runs with a `CheckpointManager`; it pauses on a `RequestPort`, captures a checkpoint at that superstep, drops the first run, then resumes a fresh workflow instance from the checkpoint and completes. A stateful `RevisionTracker` executor writes its counter into the checkpoint and reads it back.

Run without arguments, both runs happen in one process against an in-memory checkpoint manager: a **same-process simulation** that shows the mechanism and proves nothing about a crash. Nobody is at the keyboard in that demo, so the approval is **simulated**: a fixed, typed `ApprovalDecision.Approve`, printed as `SIMULATED human decision: approve`. The `prepare` and `resume` modes do it for real: two separate processes that share nothing but a checkpoint folder written by `FileSystemJsonCheckpointStore`.

## Key Concepts

| Concept | Description |
|---------|-------------|
| `CheckpointManager.CreateInMemory()` | In-memory checkpoint store, used by the single-process demo |
| `FileSystemJsonCheckpointStore` (namespace `Microsoft.Agents.AI.Workflows.Checkpointing`) | Durable store: one JSON file per checkpoint plus `index.jsonl`; `IDisposable`, locks its folder |
| `CheckpointManager.CreateJson(store)` | Checkpoint manager over a durable JSON store |
| `CheckpointManager.GetLatestCheckpointAsync(sessionId)` | The newest checkpoint for a session, read back in a new process |
| `InProcessExecution.RunStreamingAsync(wf, input, checkpointManager, sessionId, ct)` | Run with checkpointing enabled |
| `SuperStepCompletedEvent.CompletionInfo.Checkpoint` | The `CheckpointInfo` captured at a superstep boundary |
| `InProcessExecution.ResumeStreamingAsync(wf, checkpointInfo, checkpointManager, ct)` | Resume a fresh workflow instance from a checkpoint |
| `Executor.OnCheckpointingAsync` / `OnCheckpointRestoredAsync` | Persist and restore executor state |

## Running

```bash
# Single-process demo (in-memory checkpoints)
dotnet run --project src/Ch14.Workflows/Ch14.CheckpointedApproval

# Real restart: process 1 drafts, persists and exits ...
dotnet run --project src/Ch14.Workflows/Ch14.CheckpointedApproval -- prepare
# ... process 2 loads the checkpoint and finishes (approve or reject)
dotnet run --project src/Ch14.Workflows/Ch14.CheckpointedApproval -- resume <session-id> approve
```

`prepare` prints the session id to pass to `resume`. Both modes take `--store <folder>` (default: `AgenticAI/Ch14.CheckpointedApproval` under the temp folder). A missing, unknown, corrupted or already-decided checkpoint fails closed: `resume` prints `RESUME FAILED`, finalizes nothing and exits non-zero. No mode approves an empty draft: `ApprovalDecisions.TryCreateReply` refuses it, the demo prints `DEMO STOPPED` and `resume` prints `RESUME FAILED`, both with a non-zero exit. (`--empty-draft`, offline only, makes the scripted model return nothing so you can see this.)

Requires a running model provider for the drafting step. Defaults to Ollama (`llama3.2`); configure others in `appsettings.json`. Add `--offline` to any mode to draft with a scripted fake `IChatClient` instead.

## Self-test (offline)

```bash
dotnet run --project src/Ch14.Workflows/Ch14.CheckpointedApproval -- --self-test
```

Checks the `approve`/`reject` parser and the fail-closed reply, runs the demo mode (labelled as simulated, and refusing an empty draft), then launches `prepare` and `resume` as separate child processes of itself (`dotnet exec` of the same assembly, `--offline`) against a temp folder, asserts the approved and rejected results and the restored executor state, then checks that an unknown session, a missing store, a bad decision, a corrupted checkpoint file, a corrupted index, a deleted checkpoint file, an already-decided session and an empty draft all fail closed. Prints `RESULT n/n`; exits non-zero on any failure.
