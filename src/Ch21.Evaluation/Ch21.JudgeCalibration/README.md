# Judge calibration (JudgeCalibration)

Measures an LLM judge against reference labels before anyone trusts its score.

- **Versioned rubric**: `rubric/faq-support.v1.md`, four numbered lines (R1-R4).
  The judge must answer `VERDICT:` and name the `RUBRIC:` line that decided it.
- **Calibration split**: `data/calibration.v1.json`, 20 hand-written items
  (10 supported, 10 unsupported), each label tied to the rubric line it rests on.
  The answers are fixtures (`producedBy: fixture`), written with their labels;
  replace them with your own team's labels.
- **Report**: confusion matrix (positive = supported, so a false positive lets an
  unsupported answer through), agreement, Cohen's kappa, and the disagreement
  queue with both sides' rubric lines. The queue is what the next rubric version answers.
- **Who may judge**: a judge model that produced any answer it would grade is refused.
- **Prompt hygiene**: evidence and answer go between data markers; an answer's
  "note to the evaluator" is part of the answer (R4).

## Running

```bash
# Offline: a scripted judge with known replies; every number checkable by hand. RESULT n/n
dotnet run --project src/Ch21.Evaluation/Ch21.JudgeCalibration -- --self-test

# Replay the recorded llama3.2 verdicts in data/ (no model)
dotnet run --project src/Ch21.Evaluation/Ch21.JudgeCalibration

# Run the judge live (Ollama, LlmProvider:Judge:Model) and record its verdicts
dotnet run --project src/Ch21.Evaluation/Ch21.JudgeCalibration -- --live --record data/verdicts.llama3.2.faq-support.v1.json
```

Twenty items and one judge run give a rough estimate for this set and this rubric
version only.
