# Exercise 04 -- Resumable Review (Challenge)

Takes Exercise 02's review pipeline and makes it **durable**. The workflow runs an analyzer and a security scanner, then pauses on a `RequestPort` for a human sign-off. It captures a checkpoint at that pause, simulates a process crash, then resumes from the checkpoint in a second run and completes. FULLY STANDALONE (no model provider).

## Key Concepts

| Concept | Description |
|---------|-------------|
| `CheckpointManager.CreateInMemory()` | In-memory checkpoint store |
| `InProcessExecution.RunStreamingAsync(wf, input, checkpointManager, sessionId, ct)` | Run with checkpointing |
| `SuperStepCompletedEvent.CompletionInfo.Checkpoint` | The `CheckpointInfo` captured at a superstep boundary |
| `InProcessExecution.ResumeStreamingAsync(wf, checkpointInfo, checkpointManager, ct)` | Resume from a checkpoint |

## Running

```bash
dotnet run --project exercises/Ch12.Exercises/Ex04.ResumableReview
```

The worked solution is in Appendix E.
