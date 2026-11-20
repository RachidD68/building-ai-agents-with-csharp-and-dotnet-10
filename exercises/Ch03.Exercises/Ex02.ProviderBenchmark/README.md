# Exercise 02 — Provider Benchmark (Intermediate)

## Prompt

Send the **same prompt 5 times** to each configured provider and report, per provider, the time to
the **first streamed token** and the **total** time. Run it on two configurations (for example
Ollama and one hosted provider).

## Acceptance

- Reports first-token and total time for each configuration.
- Keeps failures separate: a failed call is counted, never averaged in.
- Dates the result and names the package versions it ran on.

A provider counts as configured when its required field is set: `LlmProvider:Ollama:Endpoint`,
`LlmProvider:OpenAI:ApiKey` or `LlmProvider:AzureOpenAI:Endpoint`.

## Run

```bash
dotnet run -- --self-test      # offline: the timing harness on a manual clock, RESULT n/n
dotnet run                     # 5 calls per configured provider
```

Five calls on one machine are an anecdote, not a measurement; the performance chapter measures
latency properly.
