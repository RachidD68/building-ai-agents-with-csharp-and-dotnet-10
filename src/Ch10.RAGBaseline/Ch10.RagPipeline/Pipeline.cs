using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;

namespace Ch10.RagPipeline;

/// <summary>
/// The RAG pipeline in five steps: check the model, chunk, embed, store, retrieve.
/// Program.cs runs it against a real embedding model; SelfTest.cs against fixed vectors.
/// </summary>
public static class Pipeline
{
    /// <summary>
    /// Step 0, at startup: one probe embedding, compared with the collection's
    /// declared vector size. A wrong model fails here, before any file is read.
    /// </summary>
    public static async Task CheckModelAsync(
        IEmbeddingGenerator<string, Embedding<float>> generator, CancellationToken cancellationToken = default)
    {
        Embedding<float> probe = await generator.GenerateAsync("dimension probe", cancellationToken: cancellationToken);
        DocumentChunk.CheckDimensions(probe.Vector);
    }

    /// <summary>Step 1: split by blank line; paragraphs under 20 characters are skipped.</summary>
    public static IEnumerable<DocumentChunk> Chunk(string fileName, string content)
    {
        string[] paragraphs = content.Split(
            ["\r\n\r\n", "\n\n"],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (int i = 0; i < paragraphs.Length; i++)
        {
            if (paragraphs[i].Length < 20) continue;
            yield return new DocumentChunk
            {
                Id = $"{fileName}-{i}",
                Content = paragraphs[i],
                SourceFile = fileName,
                ChunkIndex = i,
            };
        }
    }

    /// <summary>
    /// Steps 2 and 3: embed every chunk in one batch, check every vector, then
    /// store. Nothing is stored unless all vectors have the schema's size.
    /// </summary>
    public static async Task IngestAsync(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        VectorStoreCollection<string, DocumentChunk> collection,
        IReadOnlyList<DocumentChunk> chunks,
        CancellationToken cancellationToken = default)
    {
        GeneratedEmbeddings<Embedding<float>> embeddings = await generator.GenerateAsync(
            chunks.Select(c => c.Content), cancellationToken: cancellationToken);

        for (int i = 0; i < chunks.Count; i++)
        {
            chunks[i].Embedding = DocumentChunk.CheckDimensions(embeddings[i].Vector);
        }

        await collection.UpsertAsync(chunks, cancellationToken);
    }

    /// <summary>Step 4: embed the query and return the top hits, best first.</summary>
    public static async Task<List<VectorSearchResult<DocumentChunk>>> SearchAsync(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        VectorStoreCollection<string, DocumentChunk> collection,
        string query, int top = 3, CancellationToken cancellationToken = default)
    {
        Embedding<float> queryEmbedding = await generator.GenerateAsync(query, cancellationToken: cancellationToken);
        List<VectorSearchResult<DocumentChunk>> hits = [];
        await foreach (VectorSearchResult<DocumentChunk> hit in collection.SearchAsync(
            queryEmbedding.Vector, top, cancellationToken: cancellationToken))
        {
            hits.Add(hit);
        }
        return hits;
    }
}
