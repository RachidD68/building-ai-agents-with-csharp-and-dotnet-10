# Ch15.ConsumptionLimits — Denial-of-wallet & runaway loops (OWASP LLM10)

Cost and availability are **safety** concerns. An attacker or a buggy tool loop
can drive unbounded token spend or an effectively infinite function-call loop.
The agent loop needs hard caps.

## What It Shows

- **`MaximumIterationsPerRequest`** (on `FunctionInvokingChatClient`, default 40)
  — caps function-call roundtrips. A deterministic `RunawayClient` requests the
  same tool every turn and never stops; the cap terminates it cleanly after N
  calls (no exception, no infinite spin).
- **`ChatOptions.AllowMultipleToolCalls = false`** — one tool call per
  iteration, so a runaway model cannot fan out within a turn.
- **A per-request token-budget guard** — a tiny `DelegatingChatClient` that sums
  `ChatResponse.Usage.TotalTokenCount` and throws once a budget is exceeded.
- **A per-request deadline** — a `CancellationToken` with a timeout cancels a
  slow call instead of waiting forever.

The demo uses deterministic test doubles so every cap is provably exercised
without a live model. `MaximumConsecutiveErrorsPerRequest` (default 3) is the
related cap for consecutive failing tool calls.

> API-gateway / per-user rate limiting lives in **Chapter 13**. This project is
> about the **agent loop** specifically.

## Running

```bash
dotnet run
```
