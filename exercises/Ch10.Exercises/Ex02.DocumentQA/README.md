# Exercise 02: Document Q&A (Intermediate)

## Prompt

Build a Q&A agent over five short documents about a fictional company (NovaTech Inc.): embed, store, retrieve, then answer with an agent that cites the document ids it used.

## Acceptance

- An answer cites a source id, and only ids that were retrieved (`QaAnswer.CitesItsSources`).
- An out-of-corpus question abstains: nothing scores at or above the threshold, and the model is not called.
- The configured `EmbeddingDimensions`, `DocumentChunk.Dimensions` and the model's vector size must agree; startup fails otherwise. Switching to `openai` or `azure` (1536) means changing `DocumentChunk.Dimensions`.
- `dotnet run -- --self-test` passes offline (fake model, keyword embeddings).

The score threshold (`DocumentQa:ScoreThreshold`, 0.55) is a starting point; measure it as `Ch10.RetrievalEval` does.
