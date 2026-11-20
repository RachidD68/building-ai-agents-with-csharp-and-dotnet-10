# Ch15.RestartTests

Restart proof with real processes. Each case starts a worker process (`dotnet exec` of the built assembly), waits until it prints `PAUSED at <point>`, kills its process tree (no `finally` runs and the lease is not released), waits for the lease to expire, starts a new process, and counts the rows in the simulated provider's journal.

| Section | Cases |
|---|---|
| `Ch15.ExternalEffects` | killed at `before-effect`, `after-accept`, `after-record`: one effect each time, same reference, no new operation id; DEFECT REPRODUCED: the naive step after an `after-record` kill pays twice; a lost reply ends `Unknown` and a later process reconciles it; a stalled worker wakes with a stale token after another process took over (`StaleLease`, one effect); two workers started together (one effect) |
| `Ch15.DurableReview` | `prepare` exits, `review` and `resume` run in fresh processes; no mutable static field in the three assemblies (read from metadata with `System.Reflection.Metadata`, nothing loaded) |
| Contoso v5 | the same three kill points during `resume`, through the v2 desk: one committed operation, one payment |

A plain console harness with no package dependency (port a case to xUnit by moving it into a `[Fact]`). It runs the built `Ch15.ExternalEffects`, `Ch15.DurableReview` and `contoso/v5-durable-resume` assemblies of its own configuration, so build the solution first. Offline: every model is scripted. Killed workers hold an 8-second lease, so a full run takes a few minutes.

```bash
dotnet build AgenticAI.slnx -c Release
dotnet run --project src/Ch15.DurableExecution/Ch15.RestartTests -c Release --no-build
```

Prints `RESULT n/n`; exit 0 only when every check passes. Accepts `--self-test` (the same run) so the repository's self-test sweep finds it.
