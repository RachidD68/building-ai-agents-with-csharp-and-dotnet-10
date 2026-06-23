# Exercise 02 — Provider Benchmark (Intermediate)

## Prompt

Send the **same prompt** to multiple configured LLM providers and compare:

1. Response time (milliseconds)
2. Output character length
3. Token usage (input/output)
4. Display results in a formatted comparison table

## Setup

Configure providers in User Secrets. The benchmark will try each configured provider:

```bash
dotnet user-secrets set "LlmProvider:GitHub:Token" "your-token"
```

Edit the `providerNames` array in Program.cs to include only providers you have configured.

## Hints

- Use `Stopwatch` to measure elapsed time per provider.
- Wrap each call in try/catch to handle unavailable providers gracefully.
- Sort by elapsed time to identify the fastest provider.
- Token counts may be null for some providers.

## Expected Outcome

```
--- Benchmark Results ---

  Provider       Time (ms)    Chars   In Tok  Out Tok
  ------------ ---------- -------- -------- --------
  github             1250      320       35       80
  ollama             3400      295        0        0

Fastest provider: github (1250ms)
```

## Key Concepts

- Provider-agnostic `IChatClient` usage
- Performance benchmarking with `Stopwatch`
- Graceful error handling across providers
- Formatted console table output
