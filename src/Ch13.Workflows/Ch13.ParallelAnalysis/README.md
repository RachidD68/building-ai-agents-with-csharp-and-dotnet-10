# Chapter 12 -- Parallel Analysis

Idiomatic **fan-out / fan-in**. A dispatcher fans a product review out to three analyzer agents (sentiment, topics, summary) that run concurrently; a fan-in barrier holds their results until all three finish, then an aggregator assembles the report. Prints the Mermaid graph at startup.

## Key Concepts

| Concept | Description |
|---------|-------------|
| `AddFanOutEdge(source, [a, b, c])` | One source, many targets, scheduled in the same superstep (parallel) |
| `AddFanInBarrierEdge([a, b, c], target)` | Holds messages until every source has produced output, then delivers them |
| `[YieldsOutput(typeof(T))]` + `YieldOutputAsync` | Declare and emit the workflow output from the aggregator |
| `WorkflowVisualizer.ToMermaidString(workflow)` | Render the graph as Mermaid |

The barrier delivers branch results **one message at a time** (not as a list), so the aggregator counts until it has all the expected branches before it yields.

## Running

```bash
dotnet run --project src/Ch12.Workflows/Ch12.ParallelAnalysis
```

Requires a running model provider. Defaults to Ollama (`llama3.2`); configure others in `appsettings.json`.
