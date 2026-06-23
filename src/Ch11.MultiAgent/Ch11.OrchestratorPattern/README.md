# Chapter 11 -- Orchestrator Pattern

Demonstrates the hub-and-spoke multi-agent pattern. An orchestrator agent uses function calling to route queries to the right specialist: CodeExpert, DataExpert, or SecurityExpert.

## Key Concepts

| Concept | Description |
|---------|-------------|
| Agents-as-tools | Each specialist agent is wrapped as an `AIFunction` |
| `AIFunctionFactory.Create()` | Creates tools that invoke specialist agents |
| Orchestrator routing | The LLM decides which specialist(s) to consult |
| Multi-domain queries | A single question can trigger multiple specialists |

## Running

```bash
dotnet run --project src/Ch11.MultiAgent/Ch11.OrchestratorPattern
```
