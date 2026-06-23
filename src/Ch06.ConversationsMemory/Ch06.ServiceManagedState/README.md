# Ch06.ServiceManagedState — Where Does the Conversation Live?

Client-managed vs service-managed conversation state, the same two-turn conversation under each.

## What You'll Learn

- **Client-managed** — `InMemoryChatHistoryProvider` keeps the transcript in the `AgentSession.StateBag`. Serializing the session writes the full transcript; you own persistence.
- **Service-managed** — a server-backed agent (OpenAI Responses, Microsoft Foundry) keeps the transcript server-side and hands back a conversation id. You resume by passing it via `ChatOptions.ConversationId` instead of replaying messages.
- How `AgentSession.StateBag` (`.Count`, `Serialize()`) lets you inspect what is stored client-side.
- Why the provider-agnostic `AddLlmChatClient` matters: against a stateless Chat Completions provider the framework *always* manages history client-side; swap to a Responses/Foundry agent and the same code becomes service-managed.

## Run

```bash
dotnet run
```

Uses the local Ollama `llama3.2` provider by default. Because Chat Completions is a stateless wire protocol, both sections store history client-side in this run — the service-managed section demonstrates the `ConversationId` lever that a server-backed provider would populate.
