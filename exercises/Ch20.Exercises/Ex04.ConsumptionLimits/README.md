# Exercise 05 — Consumption Limits (Challenge)

## Prompt

Cap a deliberately looping agent. Give it a tool that always reports "more work
to do," set `MaximumIterationsPerRequest` to a small number, and prove from the
output that the loop stops at exactly the configured limit instead of running
forever. Then enforce per-session token and request budgets with middleware that
short-circuits once a consumption limit is reached — budgets accumulate across
calls in a session (keyed on the session id) and one session's exhaustion never
affects another.

## Key Concepts

- `MaximumIterationsPerRequest` on `FunctionInvokingChatClient` (OWASP LLM10)
- `AllowMultipleToolCalls = false` — one tool call per iteration
- Per-SESSION budgets: a ledger per `ChatOptions.ConversationId`, enforced across
  calls in that session — not a per-request check, not a global counter
- Short-circuiting: once a session's token or request budget is spent, further
  calls in that session return a refusal without reaching the model (no exception)
- Deterministic test doubles so every limit provably fires without a live model

## Run

`dotnet run` — fully offline, no model or network required. The loop must stop at
exactly the iteration cap; session-a/session-c hit their budgets and get
short-circuited while session-b/session-d keep working.
