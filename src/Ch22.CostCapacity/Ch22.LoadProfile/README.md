# Ch22.LoadProfile

A stepped load against a model behind admission control, with the clocks kept apart.

- `data/workload.*.json`: a **versioned** workload: tasks with the label an accepted answer contains, concurrency steps, requests per step, the service shape (model slots, bounded queue, busy retry-after), the tool delay, the timeout and the **declared limit** (p95 total, error rate, busy rate, accepted rate). A file without a version is refused.
- `AdmissionGate`: slots plus a bounded FIFO queue; when both are full the caller gets a busy response at once.
- `LoadRunner.RunOneAsync`: monotonic timestamps for queue, tool wait, time to first token, generation and total; usage read from the stream's `UsageContent`. No retries: a load test that retries hides the 429s it is there to count. The live client therefore gets a plain `HttpClient`, not the `AgenticAI.Common` pipeline (which retries 429s).
- `LoadRunner.RunAsync`: steps up until a step crosses the declared limit, stops, and names the last step that held, with n.

The live run prints the machine (OS, CPU, logical processors, memory visible to the process) and Ollama's version and whether the model is resident, then reports the first request apart: averaged in, a cold start disappears.

```bash
dotnet run --project src/Ch22.CostCapacity/Ch22.LoadProfile                    # live: Ollama, llama3.2
dotnet run --project src/Ch22.CostCapacity/Ch22.LoadProfile -- --json-out steps.json --usage-out usage.json
dotnet run --project src/Ch22.CostCapacity/Ch22.LoadProfile -- --offline       # scripted model, 429 beyond 3 in flight
dotnet run --project src/Ch22.CostCapacity/Ch22.LoadProfile -- --self-test
```

`--usage-out` writes the run in `Ch22.CostModel`'s usage format, with the run's wall time as workstation hours, so the cost model can price a local run.
