using Microsoft.Extensions.VectorData;

namespace Ch10.FaqAgent;

/// <summary>
/// Represents a chunk of FAQ content stored in the vector store.
/// </summary>
public sealed class FaqChunk
{
    /// <summary>
    /// Unique identifier for this chunk.
    /// </summary>
    [VectorStoreKey]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// The text content of this FAQ chunk.
    /// </summary>
    [VectorStoreData]
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// The source document this chunk originated from.
    /// </summary>
    [VectorStoreData(IsIndexed = true)]
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// The topic/title extracted from the source document.
    /// </summary>
    [VectorStoreData]
    public string Topic { get; set; } = string.Empty;

    /// <summary>
    /// Vector size, part of the collection schema. 768 matches nomic-embed-text,
    /// the local default; text-embedding-3-small (OpenAI/Azure) produces 1536.
    /// The in-memory store does not check this, but a real vector database
    /// rejects vectors of the wrong size, so the sample checks it at startup.
    /// </summary>
    public const int Dimensions = 768;

    /// <summary>Returns the vector, or fails with a clear message if its size does not match <see cref="Dimensions"/>.</summary>
    public static ReadOnlyMemory<float> CheckDimensions(ReadOnlyMemory<float> vector) =>
        vector.Length == Dimensions
            ? vector
            : throw new InvalidOperationException(
                $"The embedding model returned {vector.Length} dimensions, but FaqChunk declares {Dimensions}. " +
                "nomic-embed-text (the local default) produces 768; text-embedding-3-small produces 1536. " +
                $"Set FaqChunk.Dimensions in FaqChunk.cs to match your embedding model.");

    /// <summary>
    /// The embedding vector for this chunk.
    /// </summary>
    [VectorStoreVector(Dimensions, DistanceFunction = DistanceFunction.CosineSimilarity)]
    public ReadOnlyMemory<float> Embedding { get; set; }
}
