# Chapter 10 -- Introduction to RAG (FAQ Agent -- Part III Capstone)

The Contoso FAQ agent, grounded in a Markdown knowledge base. RAG is wired with
MAF's built-in **`TextSearchProvider`**: it runs a search before every agent call
and injects the hits as context — citations included. The agent answers from the
documents and says "I don't know" when the answer isn't in the knowledge base.

## Architecture

```
User Question
    |
    v
[TextSearchProvider runs BEFORE the call] --> FaqSearchService.SearchAsync
    |                                              |
    |                                    [Embed Query] -> [Vector Search] -> [Top-K, score-filtered]
    |                                              |
    v                                              v
[ChatClientAgent]  <--- provider injects the hits (with SourceName citations)
    |
    v
Grounded Answer  (or the "I don't know" fallback on no strong hit)
```

- `FaqSearchService` — the retrieval pipeline (embed -> search -> keep strong
  matches), the same logic taught in `Ch10.RagPipeline`, mapped to
  `TextSearchProvider.TextSearchResult`.
- `RecentMessageMemoryLimit = 6` — recent turns feed the search input, so a
  follow-up like *"what about for contractors?"* still retrieves correctly.
- The hand-rolled equivalent lives in `Ch10.FaqAgent.CustomProvider` (the
  "under the hood" version).

## FAQ Documents (11 files)

Covers: PTO, remote work, benefits, expenses, performance reviews, company
values, onboarding, engineering practices, code of conduct, sick leave,
security policy.

## Running

```bash
# Uses Ollama by default (nomic-embed-text for embeddings, llama3.2 for chat).
# Pull the embedding model once: ollama pull nomic-embed-text
dotnet run --project src/Ch10.IntroRAG/Ch10.FaqAgent
```

> Note: this project pins `Microsoft.Extensions.VectorData.Abstractions` to
> `10.1.0` via `VersionOverride`. The in-memory connector references a type
> (`VectorSearchFilter`) that was removed in 10.5.0, so `SearchAsync` throws a
> `TypeLoadException` against the higher pin. 10.1.0 has everything the chapter
> and `TextSearchProvider` need. A frontier chat model (gpt-4o-mini via the
> `github`/`openai`/`azure` providers) gives crisper grounded answers than a
> small local model.
