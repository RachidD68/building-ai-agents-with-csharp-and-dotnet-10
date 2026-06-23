using Microsoft.Extensions.VectorData;

namespace Ch17.FaqEval;

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
    /// The embedding vector for this chunk.
    /// </summary>
    [VectorStoreVector(1536, DistanceFunction = DistanceFunction.CosineSimilarity)]
    public ReadOnlyMemory<float> Embedding { get; set; }
}
