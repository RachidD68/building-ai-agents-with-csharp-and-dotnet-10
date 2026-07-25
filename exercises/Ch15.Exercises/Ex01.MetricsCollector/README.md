# Exercise 01 — Metrics Collector (Beginner)

## Prompt

Extend `Ch15.OpenTelemetryAgent` with one new metric: `agent.cache.hit_rate` as a
`Counter<long>` tagged with `cache.name`. Simulate cache hits and misses inside the
agent pipeline (this solution uses a small response cache keyed on the prompt, so
only unique prompts reach the model). Run for 100 requests and confirm the metric
shows up in the console exporter.

## Key Concepts

- `Meter` + `CreateCounter<long>("agent.cache.hit_rate")` — increment on hit, never on miss
- Tagging measurements with `cache.name` and filtering by that tag in the exporter output
- `AddOpenTelemetry().WithMetrics(...)` with `AddMeter(...)` and the console exporter
- Starting the host so the OTel `MeterListener` is installed (no listener, no data)
- A caching `DelegatingChatClient` in the agent pipeline — repeats are served from memory
