using System.Numerics.Tensors;

namespace Ex01.SimilarityExplorer;

/// <summary>
/// A corpus and its vectors. Every vector must have the same size, and a query
/// of another size is refused before any score is computed: cosine similarity
/// between vectors from two different models means nothing.
/// </summary>
public sealed class SimilarityIndex
{
    private readonly IReadOnlyList<string> _texts;
    private readonly IReadOnlyList<ReadOnlyMemory<float>> _vectors;

    public SimilarityIndex(IReadOnlyList<string> texts, IReadOnlyList<ReadOnlyMemory<float>> vectors)
    {
        if (texts.Count != vectors.Count || texts.Count == 0)
            throw new ArgumentException("One vector per text, and at least one text.");
        Dimensions = vectors[0].Length;
        if (vectors.Any(v => v.Length != Dimensions))
            throw new InvalidOperationException("The corpus vectors do not all have the same size.");
        _texts = texts;
        _vectors = vectors;
    }

    public int Dimensions { get; }

    /// <summary>The k most similar texts, best first.</summary>
    public IReadOnlyList<(string Text, float Score)> Top(ReadOnlyMemory<float> query, int k = 3)
    {
        if (query.Length != Dimensions)
            throw new InvalidOperationException(
                $"The query has {query.Length} dimensions but the corpus has {Dimensions}. " +
                "Embed the corpus and the query with the same model.");

        return [.. _texts
            .Select((text, i) => (Text: text, Score: TensorPrimitives.CosineSimilarity(_vectors[i].Span, query.Span)))
            .OrderByDescending(m => m.Score)
            .Take(k)];
    }
}
