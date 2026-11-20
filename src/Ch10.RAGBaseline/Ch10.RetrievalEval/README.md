# Ch10.RetrievalEval: measure retrieval before any model answers

An annotated set (`eval-set.json`) names the handbook chunks that answer each question: one chunk (10 questions), several chunks (7) or none (5, "not in the handbook"). The run embeds the FAQ agent's handbook with `nomic-embed-text`, ranks every chunk for every question and reports, for each score threshold from 0.40 to 0.80:

- **HitRate@K**: the share of answerable questions with at least one relevant chunk in the top K at or above the threshold;
- **Recall@K**: the average share of each answerable question's relevant chunks found there;
- how many no-answer questions abstained (nothing at or above the threshold) and how many answerable questions did.

The two metrics differ as soon as a question has more than one relevant chunk. The first edition's "recall@K" (a correct chunk appears in the top K) is HitRate@K.

The chosen threshold is the one with the most correct outcomes (hits plus correct abstentions); on a tie the lowest, and the report prints the tied range. It holds for this corpus, this model and this set only.

Chunk ids are `<document>-<paragraph index>` from `FaqIngest.Chunk`; the run stops (exit 2) if the set names a chunk that does not exist.

## Running

```bash
dotnet run --project src/Ch10.RAGBaseline/Ch10.RetrievalEval                  # Ollama nomic-embed-text, K = 3
dotnet run --project src/Ch10.RAGBaseline/Ch10.RetrievalEval -- --k 5         # another K
dotnet run --project src/Ch10.RAGBaseline/Ch10.RetrievalEval -- --self-test   # fixed 2-D vectors; RESULT n/n
```

The self-test places every chunk and question at a fixed angle, so each score is the cosine of an angle difference and every number can be checked by hand: one question with one relevant chunk against one with three, the first edition's definition next to Recall@K, and a sweep where 0.55 lets an out-of-scope question through and 0.80 drops an answerable one.

## Challenge

Add five questions with two relevant chunks each, then compare HitRate@3 and Recall@3 on the new set and justify the threshold from the sweep.
