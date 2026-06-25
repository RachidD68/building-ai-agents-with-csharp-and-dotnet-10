# Chapter 11 -- Concurrent Orchestration

Demonstrates the built-in **Concurrent** orchestration: one input fans out to N specialists that run in parallel, and an aggregator merges their results. Here three reviewers (performance, security, maintainability) analyze the same design doc at once.

## Key Concepts

| Concept | Description |
|---------|-------------|
| `AgentWorkflowBuilder.BuildConcurrent(agents, aggregator)` | Builds a `Workflow` that runs the agents in parallel |
| Aggregator | A `Func<IList<List<ChatMessage>>, List<ChatMessage>>` that combines the parallel results (here, a labelled merge) |
| `InProcessExecution.Default.RunAsync(...)` | Runs the workflow; the merged result arrives as a `WorkflowOutputEvent` whose `Data` is a `List<ChatMessage>` |
| Aggregation strategies | merge / vote / rank / pick-best |

## Running

```bash
dotnet run --project src/Ch11.MultiAgent/Ch11.ConcurrentOrchestration
```

Requires Ollama running locally with `llama3.2` (see `appsettings.json`).
