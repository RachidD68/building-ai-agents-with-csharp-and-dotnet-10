# Chapter 11 -- Handoff Orchestration

Demonstrates the built-in **Handoff** orchestration: a triage agent transfers control to the right specialist based on context. This is the production-grade version of the hand-rolled support-router challenge in the chapter exercises.

## Key Concepts

| Concept | Description |
|---------|-------------|
| `AgentWorkflowBuilder.CreateHandoffBuilderWith(triage)` | Starts the handoff graph at the triage agent |
| `WithHandoffs(from, [targets])` | Wires which agents `from` can hand off to |
| Handoffs as tool calls | Each handoff is implemented as a tool call the framework wires for you (agents must support tool calling) |
| `WorkflowOutputEvent` | The final conversation transcript arrives as a `List<ChatMessage>` |

## Running

```bash
dotnet run --project src/Ch11.MultiAgent/Ch11.HandoffOrchestration
```

Requires Ollama running locally with `llama3.2` (see `appsettings.json`).
