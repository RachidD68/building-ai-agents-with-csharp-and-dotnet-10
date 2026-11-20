# Ch06.ContextBudget — Keeping the Prompt Under a Token Budget

Without reduction, every turn sends the whole conversation again, so each call's input tokens grow with the conversation. This project measures that growth and holds it to a budget. It absorbs the first edition's `Ch06.HistoryReduction`.

## Strategies, default-first

| Strategy | Status | Where it plugs in |
|---|---|---|
| `MessageCountingChatReducer(6)` | experimental `MEAI001` | `InMemoryChatHistoryProviderOptions.ChatReducer` |
| `SummarizingChatReducer(client, 4, 2)` | experimental `MEAI001` | same slot; one extra model call when it fires (live mode only) |
| Compaction pipeline: `PipelineCompactionStrategy` of `ToolResultCompactionStrategy` and `SlidingWindowCompactionStrategy`, triggered by `CompactionTriggers.TokensExceed(300)` | experimental `MAAI001` | `CompactionProvider` in `ChatClientAgentOptions.AIContextProviders` |
| `LastTurnsChatReducer(3)`, hand-written | GA (`IChatReducer`) | `ChatReducer` |

A reducer in the `ChatReducer` slot runs before the messages are retrieved, and the provider keeps the reduced list: after 20 turns `GetMessages` returns 8 messages, not 40. `CompactionProvider` compacts what is sent to the model and leaves the stored history whole (40 messages).

## Run

```bash
dotnet run                    # the five strategies on the configured model (Ollama llama3.2), with reported input tokens
dotnet run -- --self-test     # fake client reporting UsageDetails, 20 turns per strategy; RESULT n/n
```

The self-test's token counts are estimates (UTF-8 bytes / 4, the rule the compaction index uses without a tokenizer), reported by a fake `IChatClient` through `UsageDetails.InputTokenCount`. They show the shape of the curve, not a real model's count.
