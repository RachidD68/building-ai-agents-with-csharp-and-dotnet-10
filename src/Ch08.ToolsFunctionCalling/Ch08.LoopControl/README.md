# Chapter 8 -- Loop Control (FunctionInvokingChatClient)

Supports the chapter's "Inside the loop" section. Shows the guardrails on the tool-calling loop engine, `FunctionInvokingChatClient`, configured to low limits so they fire visibly.

## Key Concepts

| Concept | Description |
|---------|-------------|
| `FunctionInvokingChatClient` | The `DelegatingChatClient` that runs the loop (call -> invoke -> result -> repeat) |
| `MaximumIterationsPerRequest` (default 40) | Caps model<->tool round-trips; on hit, the run returns without throwing |
| `MaximumConsecutiveErrorsPerRequest` (default 3) | Rethrows after N consecutive failing invocations; `0` terminates on first error |
| `IncludeDetailedErrors` (default false) | Feeds the failing tool's exception text back to the model (data-leak risk; off by default) |
| `UseFunctionInvocation(configure: ...)` | Builds a configured invocation layer on the client pipeline |
| `ChatClientAgentOptions.UseProvidedChatClientAsIs = true` | Stops the agent adding a second, default invocation layer over yours |

## Demos

1. **Iteration cap** -- a poll tool that always says "still processing"; capped at 3 round-trips, returns safely without throwing.
2. **Error guardrail** -- a ledger tool that always throws; with the consecutive-error cap at 0, the loop terminates on the first error and surfaces the original exception (`InvalidOperationException`), which MAF does not wrap.

## Running

```bash
dotnet run --project src/Ch08.ToolsFunctionCalling/Ch08.LoopControl
```
