# Exercise 01: Similarity Explorer (Basic)

## Prompt

Extend `Ch10.EmbeddingsDemo` to accept a user query from the console, embed it, and find the **three most similar sentences** from a hard-coded corpus of 20 sentences. Show the scores. A query whose vector size differs from the corpus vectors must be reported before any search.

## Acceptance

- The top 3 sentences, best first, each with its cosine score.
- A deliberate dimension mismatch is reported before searching (`SimilarityIndex.Top`).
- `dotnet run -- --self-test` passes offline (fixed vectors).

## Hints

- Embed the whole corpus in **one batched** `GenerateAsync` call.
- Use `TensorPrimitives.CosineSimilarity`, exactly like `Ch10.EmbeddingsDemo`.
- Check `query.Length` against the corpus vectors before scoring.
