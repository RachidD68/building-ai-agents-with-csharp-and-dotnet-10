# Chapter 12 -- Checkpointed Approval

A **durable** human-in-the-loop approval workflow. The workflow runs with a `CheckpointManager`; it pauses on a `RequestPort` for human approval, captures a checkpoint at that superstep, "crashes," then resumes from the checkpoint in a second run and completes. A stateful `RevisionTracker` executor persists its counter across the restart.

## Key Concepts

| Concept | Description |
|---------|-------------|
| `CheckpointManager.CreateInMemory()` | In-memory checkpoint store (swap for `CreateJson` + a durable store) |
| `InProcessExecution.RunStreamingAsync(wf, input, checkpointManager, sessionId, ct)` | Run with checkpointing enabled |
| `SuperStepCompletedEvent.CompletionInfo.Checkpoint` | The `CheckpointInfo` captured at a superstep boundary |
| `InProcessExecution.ResumeStreamingAsync(wf, checkpointInfo, checkpointManager, ct)` | Resume a fresh workflow instance from a checkpoint |
| `Executor.OnCheckpointingAsync` / `OnCheckpointRestoredAsync` | Persist and restore executor state |

For a genuine cross-process restart, replace the in-memory manager with a `FileSystemJsonCheckpointStore` (it persists checkpoints to disk; dispose it when done, as it locks the directory).

## Running

```bash
dotnet run --project src/Ch12.Workflows/Ch12.CheckpointedApproval
```

Requires a running model provider. Defaults to Ollama (`llama3.2`); configure others in `appsettings.json`.
