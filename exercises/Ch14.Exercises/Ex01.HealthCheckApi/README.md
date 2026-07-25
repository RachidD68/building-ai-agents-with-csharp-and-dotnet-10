# Exercise 01 — Health Check API (Beginner)

## Prompt

Extend Ch14.AspNetCoreHost with two custom health checks: one that verifies
the IChatClient is reachable (by sending a minimal test prompt) and another
that checks a mock dependency. Expose them at `/healthz`. Verify both show up
in the Aspire dashboard.

## Health checks

| Name            | What it probes                                                    |
|-----------------|-------------------------------------------------------------------|
| `chat-client`   | Sends "Reply with OK." with `MaxOutputTokens = 5` to the IChatClient |
| `session-store` | Write/read round-trip against a mock in-memory session store       |

## Key Concepts

- `IHealthCheck` implementations returning `HealthCheckResult.Healthy/Unhealthy`
- `AddHealthChecks().AddCheck<T>("name")` registration
- `MapHealthChecks("/healthz")` with an optional JSON response writer
- Bounded-cost liveness probe (short prompt + low `MaxOutputTokens`)

## Try it

```bash
curl http://localhost:5000/healthz
```

Run the API under the Ch14.AspireOrchestration AppHost to see both checks as
rows in the Aspire dashboard's health view.
