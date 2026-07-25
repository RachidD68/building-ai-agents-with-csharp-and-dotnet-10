# Exercise 02 — Provider Benchmark (Intermediate)

## Prompt

Write a program that takes the **same 10 questions**, asks **each of your configured providers**, and prints a table of provider → **average latency** → **average output tokens** → **estimated cost**. Use the companion `LlmSettings` record and instantiate **one `IChatClient` per provider**.

## Hints

- Swap `AddLlmChatClient` for a **loop that registers each provider under a different keyed service name** (`AddKeyedSingleton<IChatClient>(providerName, ...)`), then resolve with `GetRequiredKeyedService<IChatClient>(providerName)`.
- `Stopwatch` gives you latency.
- The Chapter 3 cost formula gives you the dollar figure: `tokens × rate / 1M`, input and output priced separately, summed over the run.
- A provider counts as *configured* when its section carries the field it cannot work without: `Ollama:Endpoint`, `GitHub:Token`, `OpenAI:ApiKey`, or `AzureOpenAI:Endpoint`.

## Setup

Configure one or more providers in `appsettings.json`, environment variables (`LlmProvider__GitHub__Token`), or User Secrets:

```bash
dotnet user-secrets set "LlmProvider:GitHub:Token" "your-token"
```

Ollama is configured out of the box (`http://localhost:11434`, `llama3.2`). The program discovers configured providers itself — there is no hardcoded provider list to edit. Ctrl+C stops the benchmark cleanly; partial results still print.

## Expected Outcome

```
=== Exercise: Provider Benchmark ===

Configured providers: github, ollama

--- github (openai/gpt-4o-mini) ---
  Q 1:    412 ms,   18 out tokens
  ...

--- Benchmark Results (10 questions per provider) ---

  Provider   Answered     Avg ms  Avg out tok  Est. cost ($)
  ---------- -------- ---------- ------------ --------------
  github           10        450         19.4       0.000031
  ollama           10       1830         24.1       0.000000

Fastest provider: github (450 ms average)
```

## Key Concepts

- Keyed DI services: one named `IChatClient` registration per provider
- Discovering configured providers from configuration instead of hardcoding
- Benchmarking with `Stopwatch`; averaging latency and token counts
- The Chapter 3 cost formula applied to real usage data
- Distinguishing user cancellation (Ctrl+C) from provider failure
