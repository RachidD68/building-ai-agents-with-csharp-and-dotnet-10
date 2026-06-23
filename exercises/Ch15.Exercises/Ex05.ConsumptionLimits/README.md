# Exercise 05 — Consumption Limits (Intermediate)

## Prompt

Cap a deliberately looping agent. Give it a tool that always reports "more work
to do," set `MaximumIterationsPerRequest` to a small number, and prove from the
output that the loop stops at exactly the configured limit. Add a per-request
token budget and show it tripping independently of the iteration cap.

## Key Concepts

- `MaximumIterationsPerRequest` on `FunctionInvokingChatClient` (OWASP LLM10)
- `AllowMultipleToolCalls = false` — one tool call per iteration
- A token-budget `DelegatingChatClient` that throws past a cumulative cap
- Deterministic test doubles so the caps provably fire without a live model

## Run

`dotnet run` — fully offline, no model or network required. The loop must stop
at exactly the iteration cap; the token guard blocks on the call that crosses
the budget.
