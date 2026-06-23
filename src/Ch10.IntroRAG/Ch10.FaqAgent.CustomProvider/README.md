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

## The wart (and why the built-in is the default)

A plain `AIContextProvider.InvokingContext` exposes the agent and session, but
**not the user's message**. So the caller has to set `CurrentQuery` before each
`RunAsync`. `TextSearchProvider` derives from `MessageAIContextProvider`, which
*does* see the request messages — so it has no such coupling. That's the main
reason the capstone leads with it.

## Running

```bash
# Uses Ollama by default (nomic-embed-text for embeddings, llama3.2 for chat).
# Pull the embedding model once: ollama pull nomic-embed-text
dotnet run --project src/Ch10.IntroRAG/Ch10.FaqAgent.CustomProvider
```

> Note: this project pins `Microsoft.Extensions.VectorData.Abstractions` to
> `10.1.0` via `VersionOverride`, matching the in-memory connector. See the
> capstone project's README for the details.
