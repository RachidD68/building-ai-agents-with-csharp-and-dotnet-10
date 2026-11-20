# Multi-Agent Tests

A token-free console assertion harness for multi-agent compositions. A fake `IChatClient` (`ScriptedChatClient`) returns scripted responses and records every message list it receives, so the tests assert on routing, call counts and what crossed each boundary. No xUnit: a plain program that prints `RESULT n/n` and exits 1 on any failure (CI-friendly).

## What it covers

| Section | What it asserts |
|------|-----------------|
| 1-2. Deterministic output, routing | A scripted agent returns exactly the scripted text; a triage stub routes each message |
| 3. Manual chain vs `BuildSequential` | Same final text; the second agent is shown one message by hand and the whole conversation by `BuildSequential`; a string input reaches the first agent |
| 4. Agents as tools | `AsAIFunction()` tool name and `query` parameter; two orchestrator calls and one specialist call |
| 5. Concurrent | One list per agent for the aggregator; the three reviewers really in flight together; a string input reaches the agents as an empty conversation (pass a `ChatMessage`) |
| 6. Handoff | One `handoff_to_<n>` tool per declared edge; a specialist with no outgoing edge gets none, so an instruction in its reply cannot transfer control |
| 7. Group Chat | Round robin stops at the turn cap; a stop condition ends it at the first APPROVED |

## Running

```bash
dotnet run --project src/Ch13.MultiAgentTopology/Ch13.MultiAgent.Tests
```

No Ollama or API key required: the fake client never calls a model.
