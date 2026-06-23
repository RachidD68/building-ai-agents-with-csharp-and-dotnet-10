using Microsoft.Extensions.VectorData;

namespace Ch10.RagPipeline;

/// <summary>
/// Represents a chunk of a document stored in the vector store.
/// Decorated with vector data attributes for automatic schema mapping.
/// </summary>
public sealed class DocumentChunk
{
    /// <summary>
    /// Unique identifier for this chunk.
    /// </summary>
    [VectorStoreKey]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// The text content of this chunk.
    /// </summary>
    [VectorStoreData]
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// The source file name this chunk came from.
    /// </summary>
    [VectorStoreData(IsIndexed = true)]
    public string SourceFile { get; set; } = string.Empty;

    /// <summary>
    /// The zero-based index of this chunk within its source document.
    /// </summary>
    [VectorStoreData]
    public int ChunkIndex { get; set; }

    /// <summary>
    /// The embedding vector for this chunk's content.
    /// Dimensions must match the embedding model (e.g., 1536 for text-embedding-3-small).
    /// </summary>
    [VectorStoreVector(1536, DistanceFunction = DistanceFunction.CosineSimilarity)]
    public ReadOnlyMemory<float> Embedding { get; set; }
}
