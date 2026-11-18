# Chapter 10 -- FAQ Agent: custom `AIContextProvider` (under the hood)

The "Going Deeper" companion to `Ch10.FaqAgent`. The capstone wires RAG with the
built-in **`TextSearchProvider`**; this project shows the **hand-rolled
`RagContextProvider`** that does the same job underneath — embed the query, run a
vector search, inject the hits as context — so you can see the mechanism plainly.

## When you'd actually reach for this

Use a custom `AIContextProvider` when the built-in doesn't give you the control
you need:

- Custom reranking (second-stage cross-encoder, semantic ranker).
- Blending several retrieval sources into one context.
- Structured or dynamically-shaped context the prompt-based formatter can't express.

## Where the query comes from

`ProvideAIContextAsync` receives an `InvokingContext` whose `AIContext` is the
context being built for this call, and its `Messages` already hold the caller's
request messages (by default only external input, so injected history is left
out). The provider takes the last user message from there. It keeps no state
between calls, so a single instance can be a DI singleton shared by concurrent
requests, which is how Chapters 14 and 16 register it.

The built-in `TextSearchProvider` is still the default in the capstone: it adds
multi-turn search input (`RecentMessageMemoryLimit`) and citations for free.

## Running

```bash
# Uses Ollama by default (nomic-embed-text for embeddings, llama3.2 for chat).
# Pull the embedding model once: ollama pull nomic-embed-text
dotnet run --project src/Ch10.IntroRAG/Ch10.FaqAgent.CustomProvider

# Offline check, no LLM or embedding model needed: the query comes from the
# request message, two concurrent calls each retrieve their own chunks, and a
# blank question retrieves nothing. Prints RESULT n/n, exits non-zero on failure.
dotnet run --project src/Ch10.IntroRAG/Ch10.FaqAgent.CustomProvider -- --self-test
```

> Note: this project pins `Microsoft.Extensions.VectorData.Abstractions` to
> `10.1.0` via `VersionOverride`, matching the in-memory connector. See the
> capstone project's README for the details.
