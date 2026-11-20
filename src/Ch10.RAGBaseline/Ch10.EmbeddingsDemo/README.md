# Ch10.EmbeddingsDemo: embeddings and cosine similarity

Generates embeddings for five sample sentences and prints their cosine similarity matrix and the most similar pairs: sentences on the same topic score higher even when they share no words.

| Concept | Description |
|---------|-------------|
| `IEmbeddingGenerator<string, Embedding<float>>` | Provider-agnostic embedding abstraction; `OllamaApiClient` implements it directly |
| `TensorPrimitives.CosineSimilarity` | Cosine similarity over two vectors of the same size |

## Running

```bash
dotnet run --project src/Ch10.RAGBaseline/Ch10.EmbeddingsDemo                  # Ollama nomic-embed-text by default
dotnet run --project src/Ch10.RAGBaseline/Ch10.EmbeddingsDemo -- --self-test   # fixed vectors, no model; RESULT n/n
```

The self-test checks cosine similarity on hand-written vectors: the same direction scores 1, orthogonal 0, opposite -1, length does not count, and two vectors of different sizes cannot be compared (`ArgumentException`).
