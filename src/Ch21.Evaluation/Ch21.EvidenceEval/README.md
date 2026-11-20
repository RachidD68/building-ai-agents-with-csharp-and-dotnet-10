# Evidence evaluation (EvidenceEval)

Scores each answer of the Contoso FAQ and claims paths on five layers, and
reports each layer on its own instead of one "groundedness" number:

| Check | Question it answers |
|---|---|
| `fact.value` | Is the stated value the expected one, with no other value and no negation? Digits and number words ("fifteen days") both count. |
| `evidence.support` | Does a cited supporting chunk hold every value the answer states? |
| `citation.issued` | Was every cited id (`doc@vN#P`) issued to this request? |
| `abstention` | Did the answer abstain exactly when no authorized evidence answers? |
| `action.allowed` | No forbidden tool call; every required call made with matching arguments. |

A layer that does not apply to a case passes with a reason starting `n/a`; the
report counts those apart. The layers are wired as `FunctionEvaluator.Create`
checks in one `LocalEvaluator`, so `AgentEvaluationResults` carries one metric per
layer. `ReleaseGate` compares two candidates on the same held-out items: wins
and losses per item, a 95 % Wilson interval, a block on any loss in a guarded
slice (permission, action), thresholds hashed before the run, and the rule of
three with items (not paraphrases) as the trials.

Cases: `cases/evidence-cases.v1.json` (evidence texts from
`Ch21.FaqEval/docs/pto-policy.md`).

## Running

```bash
# Offline: hand-written answers and fixture outcomes, no model. RESULT n/n
dotnet run --project src/Ch21.Evaluation/Ch21.EvidenceEval -- --self-test

# Live: Ollama (llama3.2) answers every case, numRepetitions 3
dotnet run --project src/Ch21.Evaluation/Ch21.EvidenceEval
dotnet run --project src/Ch21.Evaluation/Ch21.EvidenceEval -- --reps 5
```

A live run of one small model describes that run only; the gate's numbers mean
something only on a held-out set you did not tune on.
