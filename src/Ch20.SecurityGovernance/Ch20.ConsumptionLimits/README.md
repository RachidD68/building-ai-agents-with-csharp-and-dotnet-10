# Ch20.ConsumptionLimits: runaway loops and denial of wallet (OWASP LLM10:2025)

- **`MaximumIterationsPerRequest`** on `FunctionInvokingChatClient`: with a cap of
  4, a model that asks for the same tool forever (`RunawayClient`) gets 4 tool
  calls and 5 model calls; the last reply comes back with its tool call left
  unexecuted.
- **`TokenBudgetClient`** reserves each call's worst case before the call and
  refuses when it does not fit, so the budget holds under 20 concurrent callers
  (`Task.Run`). The first edition's check after the call is kept to reproduce
  its defect: the tokens are spent before the check runs.
- **A deadline** (`CancellationToken`) stops a slow call.

The edge (API gateway, rate limits) is the hosting chapter's; this is the loop.

## Running

```bash
dotnet run -- --self-test   # RESULT n/n
```
