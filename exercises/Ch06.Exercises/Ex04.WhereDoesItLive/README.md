# Ex04.WhereDoesItLive — Service-managed state (exercise)

Build two agents over the same `IChatClient` — one with an `InMemoryChatHistoryProvider`, one without — run the same two turns, then serialize both sessions and compare `StateBag.Count` and payload size.

## Goal

- Observe that against a Chat Completions backend the framework keeps history **client-side** either way, so both serialized payloads carry the transcript.
- Understand the honest caveat: `ChatOptions.ConversationId` only moves state server-side with a Responses/Foundry agent; with Ollama/GitHub Models there is no server-side conversation to point at.
- Use resolver-backed `JsonSerializerOptions` (`AgentAbstractionsJsonUtilities.DefaultOptions`) — a bare `new JsonSerializerOptions()` throws `NotSupportedException` on `SerializeSessionAsync`.

## Run

```bash
dotnet run
```

Uses local Ollama `llama3.2` by default.
