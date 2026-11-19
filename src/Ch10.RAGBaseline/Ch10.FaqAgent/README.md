# Introduction to RAG (FAQ Agent -- Part III Capstone)

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
dotnet run --project src/Ch10.RAGBaseline/Ch10.FaqAgent
```

> Note: the in-memory vector store is `CommunityToolkit.VectorData.InMemory`,
> which is built against the current `Microsoft.Extensions.VectorData.Abstractions`,
> so the project takes both versions from `Directory.Packages.props` with no
> `VersionOverride`. (Earlier builds used the Semantic Kernel InMemory connector,
> which needed `VectorData.Abstractions` held at `10.1.0`.) A frontier chat model (gpt-5.6-luna via the
> `openai`/`azure` providers) gives crisper grounded answers than a
> small local model.
