# Exercise 02 — Streaming Comparison (Intermediate)

## Prompt

Send the same prompt using both **streaming** (`RunStreamingAsync`) and **non-streaming** (`RunAsync`) modes. Measure and compare:

1. Time-to-first-token (streaming advantage)
2. Total response time
3. Number of streaming updates received

## Hints

- Use `Stopwatch` for precise timing.
- Track the first non-empty `update.Text` for time-to-first-token.
- `RunStreamingAsync` yields `AgentResponseUpdate` items.

## Expected Outcome

```
--- Comparison Summary ---

  Metric                    Non-Streaming       Streaming
  ------------------------- --------------- ---------------
  Time to first token              2150ms            380ms
  Total time                       2150ms           2340ms

  Streaming first-token speedup: 5.7x faster
```

## Key Concepts

- `RunAsync` vs `RunStreamingAsync`
- Time-to-first-token (TTFT) as a UX metric
- `AgentResponseUpdate` streaming pattern
- Performance measurement with `Stopwatch`
