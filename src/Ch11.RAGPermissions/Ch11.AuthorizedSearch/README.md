# Ch11.AuthorizedSearch

The search boundary between the FAQ agent and the index. The host authenticates the caller and builds the agent for that one request (`EvidenceAgent.ForRequest`); the search delegate puts the caller's tenant, access level and the `Active` state into the store query (`VectorSearchOptions<T>.Filter`), so a chunk the caller may not read is never ranked, returned, logged, cached or prompted. Each hit becomes an `EvidenceRecord` with a stable id (`<doc>@v<version>#<paragraph>`), recorded in the request's `EvidenceLedger`. `EvidenceCheck` delivers an answer only when every id-shaped token in it (bracketed or not, in any case) was supplied to this request, and names why otherwise (`no_authorized_evidence`, `citation_not_supplied`, `no_citation`). It proves provenance only: a caller who may read the id a hostile document plants, and received it, passes the check, so the action boundary and a semantic support check still have to hold.

Why a per-request agent: `TextSearchProvider`'s delegate receives only the query text and a token. The self-test's probes show the alternatives at MAF 1.28.0: `AgentRunOptions.AdditionalProperties` is readable in the delegate through `AIAgent.CurrentRunContext`, but the same entries also reach the chat client's `ChatOptions.AdditionalProperties` (OllamaSharp 5.4.25 leaves them out of its request body; another connector may not), and a caller kept in a field of a shared service crosses between two concurrent requests.

The corpus (`../corpus`) is synthetic: two organizations, three access levels, superseded and withdrawn versions, a management draft and a hostile annex per organization.

```bash
dotnet run --project src/Ch11.RAGPermissions/Ch11.AuthorizedSearch -- --self-test   # no model, no network; RESULT n/n
dotnet run --project src/Ch11.RAGPermissions/Ch11.AuthorizedSearch                  # Ollama: llama3.2 + nomic-embed-text
```

The live run embeds with Ollama only (`ollama pull nomic-embed-text`). `Retrieval:MinScore` (0.5) is a starting threshold for that embedding model on this corpus, not a probability. The self-test uses `HashingEmbedder`, an offline word-hashing stand-in with no notion of meaning.
