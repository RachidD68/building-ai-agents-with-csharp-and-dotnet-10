# Exercise 02 — Streaming Comparison (Intermediate)

## Prompt

Run the same prompt through `RunAsync` and `RunStreamingAsync` and plot the token-arrival timings. For streaming, record the timestamp of each update. Print a simple console bar chart showing when each token arrived. You should be able to eyeball the provider's output rate.

## Hints

- `Stopwatch.Elapsed` gives you millisecond precision.
- A bar chart is just `new string('#', elapsedMs / 50)` per token. Don't over-engineer the visualization.
- `RunStreamingAsync` yields `AgentResponseUpdate` items — record one `(elapsedMs, text)` entry per update as it arrives.

## Expected Outcome

```
--- Token-Arrival Timings ---

     412 ms | ######## "The"
     455 ms | ######### " SOLID"
     502 ms | ########## " principles"
    ...

--- Comparison Summary ---

  Metric                    Non-Streaming       Streaming
  ------------------------- --------------- ---------------
  Time to first token              2150ms            412ms
  Total time                       2150ms           2340ms

  Streaming first-token speedup: 5.2x faster
```

## Key Concepts

- `RunAsync` vs `RunStreamingAsync`
- Per-update arrival timing with `Stopwatch`
- Time-to-first-token (TTFT) as a UX metric
- Eyeballing a provider's output rate from a console bar chart
