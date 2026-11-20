# Exercise 01 — Health Check API (Basic)

## Prompt

Split the agent API's health endpoint in two. `/health/live` answers "is the
process up?" and `/health/ready` answers "can it serve?". Neither may call the
model: an orchestrator polls them every few seconds, and a probe that sends a
prompt turns every poll into a token bill.

## Accepted when

- liveness never calls the model: 100 probes, 0 model calls;
- readiness reports a failed (fake) dependency as Unhealthy, with status 503;
- the store outage does not fail liveness (the process is still alive).

## Checks

| Name            | Tag     | What it probes                                    |
|-----------------|---------|---------------------------------------------------|
| `self`          | `live`  | nothing: the process answers                      |
| `session-store` | `ready` | a write/read round trip against the session store |

## Run

```bash
dotnet run --project exercises/Ch17.Exercises/Ex01.HealthCheckApi -- --self-test
dotnet run --project exercises/Ch17.Exercises/Ex01.HealthCheckApi     # Ollama by default
curl http://localhost:5000/health/live
curl http://localhost:5000/health/ready
```

The self-test runs the API on an in-memory TestServer with a fake model that
counts its calls, so it needs no model and no network.
