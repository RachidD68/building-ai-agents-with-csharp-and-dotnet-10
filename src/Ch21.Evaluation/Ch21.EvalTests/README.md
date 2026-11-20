# Eval as a test suite (EvalTests)

A deterministic, offline assertion harness: "treat your eval suite like your
test suite" made concrete. It is a plain console harness (no xUnit, matching the
book's other `*.Tests` projects), so it drops into the solution with no extra
test packages.

## What it shows

- The **pre-response `EvaluateAsync` overload**: it scores responses you already
  have (`AgentResponse[]`) instead of running the agent. The stub
  `UninvokedChatClient` throws if anything calls the model.
- **The failure first.** A substring check (`Contains("15")`) and MAF's
  `EvalChecks.ContainsExpected()` with expected output `"15"` both pass
  "Employees get 115 days of PTO." and "Not 15 days; employees get 20 days."
  (printed as `DEFECT REPRODUCED`).
- **The fix, named for what it measures.** `pto_value_check` requires at least one
  day count and every day count to be 15. The correct answer passes; the
  confabulated "20 PTO days", "115 days" and the negation fail. It is a lexical
  value check, not a groundedness score.
- **What `numRepetitions` does** (observed on MAF 1.28 with a counting client):
  two queries with `numRepetitions: 3` make six model calls and six result items.
  Repetitions are separate items, not an average per query.

Exit code `0` when every check holds (`RESULT n/n`), `1` otherwise. The pattern
moves to xUnit unchanged: turn each check into a `[Fact]` and let
`AssertAllPassed()` throw.

## Running

```bash
# Fully offline and deterministic: no model, no network.
dotnet run --project src/Ch21.Evaluation/Ch21.EvalTests -- --self-test
```
