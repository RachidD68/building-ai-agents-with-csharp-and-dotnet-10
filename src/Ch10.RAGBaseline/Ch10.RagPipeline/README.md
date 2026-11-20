# Ch10.RagPipeline: the RAG pipeline step by step

`Pipeline.cs` holds the steps; `Program.cs` runs them over `data/*.txt` against a real embedding model.

| Step | Code |
|------|------|
| Check the model | `CheckModelAsync`: one probe embedding against `DocumentChunk.Dimensions` (768, `nomic-embed-text`), before any file is read |
| Chunk | `Chunk`: split at blank lines, skip paragraphs under 20 characters, id `<file>-<paragraph index>` |
| Embed and store | `IngestAsync`: one batched call, every vector checked, then one upsert; nothing is stored if a vector has the wrong size |
| Retrieve | `SearchAsync`: embed the query, top K by cosine similarity |

## Running

```bash
dotnet run --project src/Ch10.RAGBaseline/Ch10.RagPipeline                  # Ollama nomic-embed-text by default
dotnet run --project src/Ch10.RAGBaseline/Ch10.RagPipeline -- --self-test   # fixed vectors, no model; RESULT n/n
```

The self-test reproduces the failure the check prevents: `InMemoryCollection` stores a 1536-dimension vector under a 768-dimension schema without complaint, and the mismatch only surfaces when a 768-dimension query searches it. With the check, a 1536-dimension model fails at startup and nothing is stored.
