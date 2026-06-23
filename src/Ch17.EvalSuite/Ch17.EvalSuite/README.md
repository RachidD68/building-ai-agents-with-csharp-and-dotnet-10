# Chapter 17 -- Evaluation (EvalSuite)

Evaluates a small weather agent (one tool, `get_weather`) with three layers of
checks composed in a single `LocalEvaluator`, plus an optional, gated
`FoundryEvals` LLM-as-judge path.

## What it shows

- **`EvalChecks.KeywordCheck("F")`** — the response must contain the temperature
  unit. `KeywordCheck` is **match-all**: every keyword you pass must appear, so
  grade on a signal present in every good answer.
- **`EvalChecks.ToolCalledCheck("get_weather")`** — the agent must have invoked
  the tool.
- **`FunctionEvaluator.Create("response_length", ...)`** — a custom predicate.
- **Gated `FoundryEvals`** — when `LlmProvider:Foundry:ProjectEndpoint` is set,
  the suite also scores Relevance + Coherence with a cloud judge. With no
  endpoint, it runs `LocalEvaluator` only and prints a skip note.

The exit code is the CI contract: `0` when every local check passes, `1`
otherwise. `AgentEvaluationResults` members used: `ProviderName`, `Passed`,
`Total`, `Failed`, `AllPassed`, `Items`, `InputItems`.

## Running

```bash
# Offline by default: Ollama (llama3.2). No cloud needed.
dotnet run --project src/Ch17.EvalSuite/Ch17.EvalSuite
```

## Enabling the Foundry judge

Set `LlmProvider:Foundry:ProjectEndpoint` (and optionally `:Model`) in
`appsettings.json`, run `az login`, then re-run. The `FoundryEvals` constructor
takes an `AIProjectClient`, the judge deployment name, and the evaluator-name
constants (`FoundryEvals.Relevance`, `FoundryEvals.Coherence`, ...).
