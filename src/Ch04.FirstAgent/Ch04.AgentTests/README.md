# Ch04.AgentTests

The first agent's contracts, tested with `AgenticAI.Testing.FakeChatClient`: no model, no network. A console harness like the repo's other tests: each check prints PASS or FAIL, the run ends with `RESULT n/n`, and the exit code is non-zero when anything failed.

What it checks:

- what reaches the model: the policy instructions (in `ChatOptions.Instructions`), the question as the last message, history only with a session, a per-call option merged over the agent's defaults and gone on the next call, the same instructions from `AsAIAgent`;
- streamed runs: `Completed`, `Truncated` on finish reason `Length`, `Cancelled` with the partial text kept and the token seen by the model call, `Failed` with the exception kept as thrown, and a time budget that runs out reported as `Failed`, not `Cancelled`. Two cases reproduce a defect first: a reader that only keeps a buffer returns a cancelled run's partial text as its answer, and a stream that ends quietly after the cancel (no exception, no finish reason, which the live Ollama run did) looks Completed when judged by the stream alone; with the caller's token it is Cancelled, and after a time budget it is Failed;
- non-streamed runs: the provider's exception passes through `RunAsync` unchanged, cancellation reaches the model call, `AgentResponse.FinishReason` carries `Length`.

`--live` runs the same contracts against the configured model through `RecordingChatClient`; the answers are printed, not graded.

```bash
dotnet run --project src/Ch04.FirstAgent/Ch04.AgentTests -- --self-test
dotnet run --project src/Ch04.FirstAgent/Ch04.AgentTests -- --live        # Ollama llama3.2 by default
```

A fake's success proves wiring and contracts, not answer quality.