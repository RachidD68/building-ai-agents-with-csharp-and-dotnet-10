# Chapter 17 -- Evaluating a RAG agent (FaqEval)

Grounds the Chapter 10 Contoso FAQ agent against a **golden set**. This is the
`rag.groundedness` gate Chapter 14 points at, and the payoff for the chapter's
opening PTO-confabulation anecdote: the failure becomes a test that fails loudly
whenever an answer drifts off the source.

## What it shows

- Rebuilds the exact Chapter 10 RAG pipeline (embed -> vector search -> inject
  context via the built-in `TextSearchProvider`).
- A golden set pairing each question with the exact policy value a grounded
  answer must contain. Questions are curated against the source wording so a
  correctly grounded answer retrieves the right chunk.
- **`LocalEvaluator` (always on, offline)** — `grounded_answer` reads the aligned
  `expectedOutput` for each query and asserts the value is present;
  `no_idk_leak` asserts the agent did not fall back to "I don't have
  information" on an answerable question.
- **MEAI `GroundednessEvaluator` (gated, off by default)** — a 1-5 LLM-judged
  groundedness score against the retrieved context, wired through
  `ChatConfiguration`. It reuses the local model as the judge.

Exit code `0` when every local grounding check passes, `1` otherwise.

## Running

```bash
# Offline by default: Ollama (nomic-embed-text embeddings + llama3.2 chat).
# Pull the embedding model once: ollama pull nomic-embed-text
dotnet run --project src/Ch17.FaqEval/Ch17.FaqEval
```

## Enabling the groundedness judge

Set `LlmProvider:Judge:Enabled` to `true` in `appsettings.json`. A 3B local
model is a poor judge (expect "Inconclusive" on some items) — point
`ChatConfiguration` at a frontier model for trustworthy scores.

> Note: pins `Microsoft.Extensions.VectorData.Abstractions` to `10.1.0` via
> `VersionOverride`, same as `Ch10.FaqAgent` (the in-memory connector needs a
> type removed in 10.5.0).
