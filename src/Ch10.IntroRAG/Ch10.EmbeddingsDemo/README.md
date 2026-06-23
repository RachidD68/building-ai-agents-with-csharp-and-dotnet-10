# Chapter 10 -- Introduction to RAG (Embeddings Demo)

Generates embeddings for sample sentences and displays a cosine similarity matrix, demonstrating that semantically similar sentences score higher even when using different words.

## Key Concepts

| Concept | Description |
|---------|-------------|
| `IEmbeddingGenerator<string, Embedding<float>>` | Provider-agnostic embedding abstraction |
| `EmbeddingClient.AsIEmbeddingGenerator()` | Extension to create embedding generator from OpenAI client |
| `TensorPrimitives.CosineSimilarity` | Efficient similarity calculation via .NET numerics |
| Semantic similarity | Similar meaning yields high scores regardless of word choice |

## Configuration

Set the embedding model in user secrets:

```bash
dotnet user-secrets set "LlmProvider:AzureOpenAI:EmbeddingModel" "text-embedding-3-small"
```

## Running

```bash
dotnet run --project src/Ch10.IntroRAG/Ch10.EmbeddingsDemo
```
