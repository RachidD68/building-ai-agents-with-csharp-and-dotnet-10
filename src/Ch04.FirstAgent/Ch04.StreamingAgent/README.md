# Ch04.StreamingAgent

The policy agent, streamed, with an explicit terminal status.

- `AgentRuns.cs`: `RunStatus` (`Completed`, `Cancelled`, `Truncated`, `Failed`), `RunOutcome` and `RunTracker`. A run ends in exactly one state. Text that arrived before a cancellation or a cut-off is kept in `RunOutcome.Text`, but only `Completed` is a success. An `OperationCanceledException` the caller did not ask for (a time budget, an HTTP timeout) is `Failed`, not `Cancelled`; any other exception is `Failed` and kept as thrown in `RunOutcome.Error`. A stream that ends with no exception after its token was cancelled is decided by the tokens too: `Cancelled` for the caller's token, `Failed` for the run's own token (`runToken`, a time budget).
- `Program.cs`: one streamed answer (press Ctrl+C during it to see `cancelled`), one with a 12-token ceiling (`truncated`), and one stream merged into a single `AgentResponse` with `ToAgentResponseAsync`.

```bash
dotnet run --project src/Ch04.FirstAgent/Ch04.StreamingAgent
```

`AgentRuns.cs` is tested offline in `Ch04.AgentTests`.