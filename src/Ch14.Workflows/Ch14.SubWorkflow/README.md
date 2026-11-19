# Chapter 12 -- Sub-workflow & Workflow-as-Agent

Two **composition** patterns. Part 1 embeds an inner text-stats workflow as a node in a parent workflow via `BindAsExecutor()`. Part 2 exposes an agent pipeline as a single `AIAgent` via `AsAIAgent()`.

## Key Concepts

| Concept | Description |
|---------|-------------|
| `innerWorkflow.BindAsExecutor("id")` | Embed a workflow as a node in a parent; typed I/O is preserved |
| `innerWorkflow.AsAIAgent()` | Expose a whole workflow as an `AIAgent` (host it or nest it) |
| `AgentWorkflowBuilder.BuildSequential(...)` | Build the agent pipeline that Part 2 turns into an agent |

`AsAIAgent()` requires the workflow's entry executor to speak the chat protocol (accept `List<ChatMessage>` + `TurnToken`). Agent-based workflows satisfy this; a plain string-typed workflow does not.

## Running

```bash
dotnet run --project src/Ch12.Workflows/Ch12.SubWorkflow
```

Part 1 is deterministic. Part 2 requires a running model provider — defaults to Ollama (`llama3.2`); configure others in `appsettings.json`.
