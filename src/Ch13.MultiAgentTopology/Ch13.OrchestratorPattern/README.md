# Orchestrator and specialists

An orchestrator agent routes each question to one or more specialists (CodeExpert, DataExpert, SecurityExpert). Each specialist is exposed to it as a tool with `AsAIFunction()`; the tool takes the specialist's name and description and one `query` parameter.

Agents as tools is one composition style among several. A workflow (`BuildSequential`, `BuildConcurrent`, Handoff) passes messages between agents directly, with no tool call in between.

A counting client under each agent prints the model calls per question: at least two for the orchestrator (choose the tools, then write the answer) on top of the specialists' calls.

## Running

```bash
dotnet run --project src/Ch13.MultiAgentTopology/Ch13.OrchestratorPattern
```

The offline proof (two orchestrator calls and one specialist call for a single-domain question; what the specialist is shown) is in `Ch13.MultiAgent.Tests`.
