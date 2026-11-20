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
    /// Vector size, part of the collection schema. 768 matches nomic-embed-text,
    /// the local default; text-embedding-3-small (OpenAI/Azure) produces 1536.
    /// Many vector databases validate the declared size; the in-memory store
    /// does not, so the sample checks it at startup.
    /// </summary>
    public const int Dimensions = 768;

    /// <summary>Returns the vector, or fails with a clear message if its size does not match <see cref="Dimensions"/>.</summary>
    public static ReadOnlyMemory<float> CheckDimensions(ReadOnlyMemory<float> vector) =>
        vector.Length == Dimensions
            ? vector
            : throw new InvalidOperationException(
                $"The embedding model returned {vector.Length} dimensions, but DocumentChunk declares {Dimensions}. " +
                "nomic-embed-text (the local default) produces 768; text-embedding-3-small produces 1536. " +
                $"Set DocumentChunk.Dimensions in DocumentChunk.cs to match your embedding model.");

    /// <summary>
    /// The embedding vector for this chunk's content.
    /// Dimensions must match the embedding model: see <see cref="Dimensions"/>.
    /// </summary>
    [VectorStoreVector(Dimensions, DistanceFunction = DistanceFunction.CosineSimilarity)]
    public ReadOnlyMemory<float> Embedding { get; set; }
}
