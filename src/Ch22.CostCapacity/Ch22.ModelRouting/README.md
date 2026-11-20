# Ch22.ModelRouting

Routing and failover, judged on **accepted tasks**. Every candidate runs the same held-out tasks (`data/tasks.heldout.json`) and gets the same report: accepted, attempts (failed ones included), cost per accepted task from the artificial price table, p95, and for the router its misroutes against each task's true difficulty.

| Candidate | Status |
|---|---|
| `OrderedFailoverChatClient` | **Experimental** (`MEAI001`) in Microsoft.Extensions.AI 10.11.0. Suppressed at each call site only. |
| `GaFailoverChatClient` | GA alternative, written on `DelegatingChatClient`. Non-streaming only. |
| `DifficultyRouterChatClient` | GA, on `DelegatingChatClient`; records every decision. |

Adoption rule (`CandidateReport.VerdictAgainst`): no fewer accepted tasks, a lower cost per accepted task, and a p95 no more than 10% higher, on the same tasks. A failover is judged against the same provider trouble without it.

One retry owner: the failover. The self-test reproduces what happens when a tier also retries (4 attempts to a provider that is down, instead of 1). It also checks the failover contract (no failover on a caller's cancellation, none once streamed output is exposed, `MaximumAttemptsPerRequest`) and shows that `DistributedCachingChatClient` makes a second pass instant without changing how many tasks are accepted.

```bash
dotnet run --project src/Ch22.CostCapacity/Ch22.ModelRouting                   # offline, scripted tiers
dotnet run --project src/Ch22.CostCapacity/Ch22.ModelRouting -- --live         # Ollama: llama3.2, then behind a dead primary
dotnet run --project src/Ch22.CostCapacity/Ch22.ModelRouting -- --self-test
```

The live run has one local model, so it measures failover only; the difficulty router runs offline.
