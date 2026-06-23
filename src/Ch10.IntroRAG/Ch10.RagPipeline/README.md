# Chapter 10 -- Introduction to RAG (Full RAG Pipeline)

Demonstrates the complete Retrieval-Augmented Generation pipeline: Ingest, Chunk, Embed, Store, Retrieve.

## Pipeline Stages

| Stage | Description |
|-------|-------------|
| **Ingest** | Load `.txt` files from the `data/` directory |
| **Chunk** | Split documents by paragraph boundaries |
| **Embed** | Generate vector embeddings via `IEmbeddingGenerator` |
| **Store** | Upsert chunks into an `InMemoryCollection` vector store |
| **Retrieve** | Search by cosine similarity against a query embedding |

## Key Concepts

| Concept | Description |
|---------|-------------|
| `VectorStoreCollection<TKey, TRecord>` | Abstract base for vector store collections |
| `InMemoryCollection` | In-memory vector store from Semantic Kernel |
| `[VectorStoreKey/Data/Vector]` | Attributes for schema mapping |
| `SearchAsync()` | Vector similarity search with top-K results |

## Running

```bash
dotnet run --project src/Ch10.IntroRAG/Ch10.RagPipeline
```
