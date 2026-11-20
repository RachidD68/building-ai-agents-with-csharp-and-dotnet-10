# Exercise 02: Streaming Comparison (Intermediate)

## Prompt

Run the same prompt through `RunAsync` and `RunStreamingAsync`. Report the time to the first output and the total time for each, and print each update's arrival time as it comes in.

## Acceptance

- The report gives time to first update and total time.
- A cancelled run is reported as cancelled, with the number of characters that arrived, never as a short success with a total time.

## Hints

- `Stopwatch` for the timings; `AgentRuns.CollectAsync` (from `Ch04.StreamingAgent`) gives the terminal state and calls you back for each piece of text.
- For a non-streamed run, the first output is the whole answer.

## Running

```bash
dotnet run --project exercises/Ch04.Exercises/Ex02.StreamingComparison
dotnet run --project exercises/Ch04.Exercises/Ex02.StreamingComparison -- --self-test
```

The self-test's timings come from a fake model and change from run to run; the checks compare them, they do not print fixed numbers.