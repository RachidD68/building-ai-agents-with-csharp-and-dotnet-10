# Ch11.HybridRetrieval

Lexical (BM25) and vector candidates, fused by reciprocal rank (`1 / (60 + rank)`), then a deterministic reranker behind `IReranker` (the stand-in for a hosted one). Each stage is measured with HitRate@K and Recall@K on a small labeled set (`hybrid-set.json`) whose chunk text comes from the chapter's corpus and whose vectors are fixed, so no embedding model is involved.

What the self-test checks: an identifier that only lexical search finds, a paraphrase that only vector search finds, Recall@K never above HitRate@K, the two equal on one-evidence questions and different on two-evidence ones, the evidence lost between the candidate set and the final top K, and the first attempt at fusion (adding raw scores) changing its answers when one engine's scores are rescaled, while rank fusion does not.

```bash
dotnet run --project src/Ch11.RAGPermissions/Ch11.HybridRetrieval -- --self-test   # RESULT n/n
```

The measures describe this set only; they are printed by the run, not quoted from anywhere.
