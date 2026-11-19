# Chapter 11 -- Group Chat Orchestration

Demonstrates the built-in **Group Chat** orchestration: several agents collaborate in a shared conversation, taking turns under a manager. Here a Writer and a Reviewer iterate on a product tagline. The framework synchronizes context for you -- no hand-rolled `List<ChatMessage>` like the Debate exercise.

## Key Concepts

| Concept | Description |
|---------|-------------|
| `AgentWorkflowBuilder.CreateGroupChatBuilderWith(managerFactory)` | Builds the group chat from a factory that creates the manager |
| `RoundRobinGroupChatManager(agents, shouldTerminateFunc)` | Alternates speakers; `MaximumIterationCount` bounds the loop |
| `AddParticipants([...])` | Registers the agents in the conversation |
| Automatic context sync | Each agent sees the full conversation before its turn |

## Running

```bash
dotnet run --project src/Ch11.MultiAgent/Ch11.GroupChatOrchestration
```

Requires Ollama running locally with `llama3.2` (see `appsettings.json`).
