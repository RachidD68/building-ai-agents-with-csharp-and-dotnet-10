# Ex03.WindowedChat — Built-in Reducer (Reducer exercise)

Wire a built-in `MessageCountingChatReducer` into the history provider, run 15 turns, and watch input tokens plateau while the stored history keeps growing.

## Goal

- Use a **built-in** reducer (not a hand-rolled one): `InMemoryChatHistoryProviderOptions.ChatReducer = new MessageCountingChatReducer(targetCount: 6)`.
- Log `AgentResponse.Usage.InputTokenCount` each turn.
- Confirm that with the default `BeforeMessagesRetrieval` trigger both input tokens *and* `historyProvider.GetMessages(session).Count` plateau once the window fills — the provider keeps the reduced view, so the running history converges to the window size instead of growing without bound.

`MessageCountingChatReducer` is `[Experimental("MEAI001")]`; the `.csproj` opts in via `<NoWarn>`.

## Run

```bash
dotnet run
```

Uses local Ollama `llama3.2` by default.
