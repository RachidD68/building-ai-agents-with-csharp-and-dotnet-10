# Ch06.HistoryReduction — Keeping History Under the Window

Four ways to keep conversation history from outgrowing the model's context window, default-first.

## What You'll Learn

- `MessageCountingChatReducer(targetCount)` — built-in, count-based trimming `[MEAI001]`
- `SummarizingChatReducer(chatClient, targetCount, threshold)` — built-in, LLM summarization `[MEAI001]`
- The compaction pipeline — `PipelineCompactionStrategy` composing `TruncationCompactionStrategy` / `ToolResultCompactionStrategy`, gated by `CompactionTriggers`, bridged in via `ChatStrategyExtensions.AsChatReducer()` `[MAAI001]`
- A hand-rolled `IChatReducer` as the escape hatch (GA contract)

A reducer attaches to `InMemoryChatHistoryProviderOptions.ChatReducer` and runs `BeforeMessagesRetrieval` by default — it trims what is sent to the model, leaving the stored history intact (so `GetMessages` still returns everything).

## Experimental APIs

`MessageCountingChatReducer` and `SummarizingChatReducer` are `[Experimental("MEAI001")]`; the entire `Microsoft.Agents.AI.Compaction` namespace is `[Experimental("MAAI001")]`. This project opts in via `<NoWarn>` in the `.csproj`.

## Run

```bash
dotnet run
```

Uses the local Ollama `llama3.2` provider by default (see `appsettings.json`).
