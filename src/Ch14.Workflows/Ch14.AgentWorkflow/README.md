# Chapter 12 -- Agent Workflow

Orchestrates **agents** as workflow executors. A Drafter agent and a Reviewer agent are wired into a graph; the workflow runtime manages the `TurnToken` handshake so each agent takes its turn in order.

## Key Concepts

| Concept | Description |
|---------|-------------|
| `AgentWorkflowBuilder.BuildSequential(...)` | One-liner that wires agents into a sequential pipeline |
| `ExecutorBindingExtensions.BindAsExecutor(agent, options)` | Wraps an `AIAgent` as a host executor node |
| `AIAgentHostOptions` | Controls event emission (`EmitAgentResponseEvents`, `EmitAgentUpdateEvents`) |
| `TurnToken` | Signals an agent executor to process its cached input and take a turn |
| `AgentResponseUpdateEvent` | Token-by-token streaming updates from an agent node |

## Running

```bash
dotnet run --project src/Ch12.Workflows/Ch12.AgentWorkflow
```

Requires a running model provider. Defaults to Ollama (`llama3.2`); configure others in `appsettings.json`.
