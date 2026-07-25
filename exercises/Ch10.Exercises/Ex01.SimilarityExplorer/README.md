# Exercise 01 — Similarity Explorer (Basic)

## Prompt

Extend `Ch10.EmbeddingsDemo` to accept a user query from the console, embed it, and find the **three most similar sentences** from a small hard-coded corpus of 20 sentences. Show the scores.

## Hints

- Embed the whole corpus in **one batched** `GenerateAsync` call — one round-trip, not twenty.
- Use `TensorPrimitives.CosineSimilarity` (`System.Numerics.Tensors`) for the scoring, exactly like `Ch10.EmbeddingsDemo`.
- Rank with `OrderByDescending(...).Take(3)` and print the scores.

## Expected Outcome

```
Query: "My kitten loves sleeping on soft furniture."
  1. 0.71xx  "A kitten was resting on the rug."
  2. 0.63xx  "The cat sat on the mat."
  3. 0.3xxx  ...
```

## Key Concepts

- Batched embedding generation with `IEmbeddingGenerator`
- Cosine similarity via SIMD-accelerated `TensorPrimitives`
- Top-k ranking over an embedded corpus (retrieval in miniature)
- Semantic meaning vs lexical matching
