# Chapter 17 -- Eval as a test suite (EvalTests)

A deterministic, offline assertion harness — "treat your eval suite like your
test suite" made concrete. It is a plain console harness (no xUnit, matching the
book's other `*.Tests` projects), so it drops into the solution with no extra
test packages.

## What it shows

- The **pre-response `EvaluateAsync` overload**: it scores responses you already
  have (`AgentResponse[]`) instead of running the agent. No model is called, so
  the run is fast, free, and 100% deterministic.
- The stub `UninvokedChatClient` throws if anything ever invokes the model —
  proof the suite never leaves the offline path.
- Two cases prove the grounding check both passes and bites:
  1. a grounded PTO answer ("...15 PTO days...") -> `AllPassed` is true;
  2. the confabulated answer from the chapter's opening ("...20 PTO days...") ->
     the check catches it, `AllPassed` is false.

The pattern moves to xUnit unchanged: turn each `Case` into a `[Fact]` and let
`AssertAllPassed()` throw the assertion. From there, Test Explorer and your CI
test reporter pick it up with no extra wiring.

Exit code `0` when every assertion holds, `1` otherwise.

## Running

```bash
# Fully offline and deterministic — no model, no network.
dotnet run --project src/Ch17.EvalTests/Ch17.EvalTests
```
