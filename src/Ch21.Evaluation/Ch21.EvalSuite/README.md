# Evaluation (EvalSuite)

Evaluates a small weather agent (one tool, `get_weather`) with three layers of
checks composed in a single `LocalEvaluator`, plus an optional, gated
`FoundryEvals` LLM-as-judge path.

## What it shows

- **`temperature_stated`** (a `FunctionEvaluator.Create` check): the answer states a
  Fahrenheit temperature as a number ("55F"). It replaces the first edition's
  `EvalChecks.KeywordCheck("F")`: the self-test shows that keyword passing
  "Fog maybe? I could not reach the weather service." (`DEFECT REPRODUCED`).
  `KeywordCheck` is match-all: every keyword you pass must appear.
- **`EvalChecks.ToolCalledCheck("get_weather")`**: the agent must have invoked the tool.
- **`response_length`**: a custom predicate, not empty and not runaway.
- **Gated `FoundryEvals`**: when `LlmProvider:Foundry:ProjectEndpoint` is set, the
  suite also scores Relevance and Coherence with a cloud judge. `FoundryEvals`
  lives in `Microsoft.Agents.AI.Foundry`, a **preview** package; the project client
  comes from `AgenticAI.Common.Foundry` (`FoundryProjects.TryCreate`). With no
  endpoint it prints a skip note and scores locally only. This path is compiled
  but was not run for the book (no Foundry credentials).

The exit code is the CI contract: `0` when every local check passes, `1`
otherwise. `AgentEvaluationResults` members used: `ProviderName`, `Passed`,
`Total`, `Failed`, `AllPassed`, `Items`, `InputItems`.

## Running

```bash
# Local model: Ollama (llama3.2). No cloud needed.
dotnet run --project src/Ch21.Evaluation/Ch21.EvalSuite

# Offline: a scripted model drives the same agent and checks. RESULT n/n
dotnet run --project src/Ch21.Evaluation/Ch21.EvalSuite -- --self-test
```

## Enabling the Foundry judge

Set `LlmProvider:Foundry:ProjectEndpoint` (and optionally `:Model`) in
`appsettings.json`, run `az login`, then re-run. The `FoundryEvals` constructor
takes an `AIProjectClient`, the judge deployment name, and the evaluator-name
constants (`FoundryEvals.Relevance`, `FoundryEvals.Coherence`, ...).
