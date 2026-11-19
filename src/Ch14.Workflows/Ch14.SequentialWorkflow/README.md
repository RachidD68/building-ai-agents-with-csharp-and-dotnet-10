# Chapter 12 -- Sequential Workflow

Demonstrates the Workflow engine from `Microsoft.Agents.AI.Workflows`. Two class-based executor nodes (Uppercase and Reverse) are wired in sequence using `WorkflowBuilder` and executed with both `RunAsync` (batch) and `RunStreamingAsync` (streaming).

## Key Concepts

| Concept | Description |
|---------|-------------|
| `WorkflowBuilder` | Builds a directed graph of executor nodes |
| `Executor<TInput, TOutput>` | Base class for a typed executor; override `HandleAsync` |
| `BindExecutor()` | Turns an executor instance into a graph-node binding |
| `AddEdge()` | Connects executors in sequence |
| `InProcessExecution.Default` | Runs workflows in-process |
| `RunAsync` / `RunStreamingAsync` | Batch and streaming execution modes |
| `WorkflowVisualizer` | Generates Mermaid diagrams of the workflow graph |

## Running

```bash
dotnet run --project src/Ch12.Workflows/Ch12.SequentialWorkflow
```
