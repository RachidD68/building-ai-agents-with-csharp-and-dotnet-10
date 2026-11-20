# Ch10.FaqAgent: a grounded RAG baseline

The Contoso FAQ agent, grounded in a Markdown handbook (`docs/`, 11 files). It answers from retrieved handbook excerpts, cites their source, and abstains when nothing relevant is retrieved.

```
ingest (once):   CheckModelAsync (768 dimensions) -> chunk by paragraph -> embed (one batch) -> store
query (per call): context provider -> FaqSearchService.RetrieveAsync
                  (embed question -> vector search top 3 -> keep score >= threshold)
                  -> excerpts with [source] -> model -> cited answer, or the abstention text
```

- `FaqIngest`: the dimension check runs on a probe text before any document is read, so a model with another vector size fails at startup.
- `FaqSearchService`: stateless retrieval; the question is an argument. The threshold (0.55 here) is this corpus's starting point with `nomic-embed-text`, not a rule; `Ch10.RetrievalEval` sweeps it.
- `FaqAgentFactory`: one place that wires the agent for the live run and the self-test.
- `FaqContextProvider`: the custom provider. It derives from `MessageAIContextProvider` and takes the question from `InvokingContext.RequestMessages`. It has no mutable field, so one instance is safe as a singleton. (It replaces the first edition's `Ch10.FaqAgent.CustomProvider`, whose `CurrentQuery` property the caller had to set before each call.)

## Running

```bash
dotnet run --project src/Ch10.RAGBaseline/Ch10.FaqAgent                          # TextSearchProvider, search before each call
dotnet run --project src/Ch10.RAGBaseline/Ch10.FaqAgent -- --provider on-demand  # TextSearchProvider as a search tool
dotnet run --project src/Ch10.RAGBaseline/Ch10.FaqAgent -- --provider custom     # FaqContextProvider
dotnet run --project src/Ch10.RAGBaseline/Ch10.FaqAgent -- --self-test           # offline; RESULT n/n
```

Ollama by default: `llama3.2` for chat and `nomic-embed-text` (768 dimensions) for embeddings. With `openai` or `azure`, set the embedding model and change `FaqChunk.Dimensions` to its size; the startup check tells you when they differ.

## What the self-test proves

A keyword embedding generator and a fake model replace both models, so the checks prove what was retrieved and what reached the model, not how a real model uses the evidence:

- the dimension check refuses a 1536-dimension model before ingestion;
- `TextSearchProvider` searches with the caller's question, injects one message, and the answer cites the source; an out-of-handbook question injects nothing and abstains;
- `RecentMessageMemoryLimit` puts earlier user questions (not the answers) into the search input: a follow-up finds its topic, and an earlier topic also rides along into a new question;
- on-demand mode offers a `search_handbook` tool and searches nothing before the call;
- `RequestMessages` carries only this call's question;
- **defect reproduced**: the first edition's shared provider with a mutable `CurrentQuery` answers one request from another request's chunks when two run at once;
- the fix: eight concurrent questions on one shared agent (both providers) each see only their own chunks, with all eight searches in flight at the same time.
