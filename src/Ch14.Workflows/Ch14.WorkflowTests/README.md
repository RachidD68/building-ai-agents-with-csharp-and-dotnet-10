# Chapter 12 -- Workflow Tests

Demonstrates **design principle #6**: cover every branch with a test. A plain console assertion harness (no xUnit dependency) builds a keyword sentiment router and runs it with inputs that exercise each of the three branches, asserting on the streamed events and the final output.

## Key Concepts

| Concept | Description |
|---------|-------------|
| `InProcessExecution.RunStreamingAsync(...)` | Run the graph in-process and observe events |
| `ExecutorCompletedEvent.ExecutorId` | Assert which branch actually ran |
| `WorkflowOutputEvent.Data` | Assert on the final output |
| Deterministic executor | A keyword classifier (no live model) keeps tests fast and repeatable |

The pattern moves directly to xUnit/NUnit: turn each `Case(...)` into a `[Fact]` and replace `Check(...)` with `Assert`.

## Running

```bash
dotnet run --project src/Ch12.Workflows/Ch12.WorkflowTests
```

Exit code `0` means all branch tests passed; non-zero means a failure. Fully deterministic — no model provider required.
