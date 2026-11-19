# Exercise 01 -- Resumable Review (Challenge)

Takes the workflows chapter's `Ex02.ReviewPipeline` and makes it **durable**. The workflow runs an analyzer and a security scanner, then pauses on a `RequestPort` for a human sign-off. It captures a checkpoint at that pause, simulates a process crash, then resumes from the checkpoint in a second run and completes. FULLY STANDALONE (no model provider).

The `prepare` and `resume` modes make the restart real: two separate processes that share nothing but a checkpoint folder written by `FileSystemJsonCheckpointStore`.

## Key Concepts

| Concept | Description |
|---------|-------------|
| `CheckpointManager.CreateInMemory()` | In-memory checkpoint store (single-process demo) |
| `FileSystemJsonCheckpointStore` + `CheckpointManager.CreateJson(store)` | Durable checkpoints on disk (namespace `Microsoft.Agents.AI.Workflows.Checkpointing`) |
| `CheckpointManager.GetLatestCheckpointAsync(sessionId)` | Find the session's newest checkpoint in a new process |
| `InProcessExecution.RunStreamingAsync(wf, input, checkpointManager, sessionId, ct)` | Run with checkpointing |
| `SuperStepCompletedEvent.CompletionInfo.Checkpoint` | The `CheckpointInfo` captured at a superstep boundary |
| `InProcessExecution.ResumeStreamingAsync(wf, checkpointInfo, checkpointManager, ct)` | Resume from a checkpoint |

## Running

```bash
# Single-process demo
dotnet run --project exercises/Ch15.Exercises/Ex01.ResumableReview

# Real restart across two processes
dotnet run --project exercises/Ch15.Exercises/Ex01.ResumableReview -- prepare
dotnet run --project exercises/Ch15.Exercises/Ex01.ResumableReview -- resume <session-id> approve

# Offline cross-process self-test (prints RESULT n/n)
dotnet run --project exercises/Ch15.Exercises/Ex01.ResumableReview -- --self-test
```

Both modes take `--store <folder>` (default: `AgenticAI/Ex01.ResumableReview` under the temp folder). A missing, unknown, corrupted or already signed-off checkpoint fails closed: `resume` prints `RESUME FAILED`, signs nothing off and exits non-zero.

The single-process demo has nobody at the keyboard, so its sign-off is a fixed, typed `SignOffDecision` printed as `SIMULATED reviewer decision: approve`. No mode approves a sign-off request with empty findings: the demo stops with `DEMO STOPPED` and `resume` fails closed (`--empty-findings` makes the scanner return nothing, to show it). The self-test covers both and runs 21 checks.

The worked solution is in Appendix E.
