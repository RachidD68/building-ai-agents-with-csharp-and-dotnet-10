# Evaluating a RAG agent (FaqEval)

Checks the RAG chapter's Contoso FAQ agent against a **golden set**. This is the
nightly evaluation gate the observability chapter points at, and the payoff for the chapter's
opening PTO-confabulation anecdote: the failure becomes a test that fails loudly
whenever an answer drifts off the source.

## What it shows

- Rebuilds the exact FAQ agent RAG pipeline (embed -> vector search -> inject
  context via the built-in `TextSearchProvider`).
- A golden set pairing each question with the exact policy value a grounded
  answer must contain. Questions are curated against the source wording so a
  correctly grounded answer retrieves the right chunk.
- **`LocalEvaluator` (always on)**: `expected_value` reads the aligned
  `expectedOutput` for each query and asserts the value is present as a whole
  token and not negated ("15" matches "15 days", not "115 days" and not
  "Not 15 days; employees get 20"). It is a lexical value check, not a
  groundedness or faithfulness score;
  `no_idk_leak` asserts the agent did not fall back to "I don't have
  information" on an answerable question.
- **MEAI `GroundednessEvaluator` (gated, off by default)** — a 1-5 LLM-judged
  groundedness score against the retrieved context (the actual groundedness
  measure), wired through
  `ChatConfiguration`. The judge must be a different model from the agent's: a
  model grading its own answers is lenient, so the sample refuses that pairing.

Exit code `0` when every local value check passes, `1` otherwise.

## Running

```bash
# Offline by default: Ollama (nomic-embed-text embeddings + llama3.2 chat).
# Pull the embedding model once: ollama pull nomic-embed-text
dotnet run --project src/Ch21.Evaluation/Ch21.FaqEval
```

## Enabling the groundedness judge

Set `LlmProvider:Judge:Enabled` to `true` and `LlmProvider:Judge:Model` to an
Ollama model other than the agent's (`LlmProvider:Ollama:Model`). With the same
model, or with another provider, the judge is skipped and the reason printed.
Calibrate the judge against human labels first (`Ch21.JudgeCalibration`): its
score means little before that.

```bash
# Offline: the value check and the judge pairing rule. RESULT n/n
dotnet run --project src/Ch21.Evaluation/Ch21.FaqEval -- --self-test
```

> Note: like `Ch10.FaqAgent`, this project uses `CommunityToolkit.VectorData.InMemory`
> and takes `Microsoft.Extensions.VectorData.Abstractions` from
> `Directory.Packages.props`; there is no `VersionOverride`.
