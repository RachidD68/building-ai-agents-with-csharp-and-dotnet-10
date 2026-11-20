using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Ch11.AuthorizedSearch;

/// <summary>
/// An offline stand-in for an embedding model, for the self-tests only: each word
/// (lowercased, stop words dropped) is hashed into one of <see cref="Dimensions"/>
/// slots and the vector is normalized. Texts that share words get similar vectors;
/// it knows nothing about meaning. Two instances with different seeds behave like
/// two different models: same vector length, unrelated geometry.
/// </summary>
public sealed partial class HashingEmbedder(int seed = 0) : IEmbeddingGenerator<string, Embedding<float>>
{
    public const int Dimensions = 768;

    public string ModelId => $"hashing-{seed}";

    private static readonly HashSet<string> s_stopWords =
    [
        "a", "an", "and", "the", "of", "on", "or", "to", "for", "in", "is", "it", "my", "i",
        "can", "do", "does", "what", "how", "be", "at", "by", "with", "that", "this", "are",
    ];

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        GeneratedEmbeddings<Embedding<float>> result = [.. values.Select(v => new Embedding<float>(Embed(v)))];
        return Task.FromResult(result);
    }

    public float[] Embed(string text)
    {
        float[] vector = new float[Dimensions];
        foreach (string word in Words(text))
        {
            vector[Slot(word)] += 1f;
        }
        float norm = MathF.Sqrt(vector.Sum(x => x * x));
        if (norm > 0)
        {
            for (int i = 0; i < vector.Length; i++) vector[i] /= norm;
        }
        return vector;
    }

    public static IEnumerable<string> Words(string text) =>
        WordPattern().Matches(text.ToLowerInvariant())
            .Select(m => m.Value.TrimEnd('s'))
            .Where(w => w.Length > 1 && !s_stopWords.Contains(w));

    // FNV-1a over the word and the seed: stable across runs and platforms,
    // unlike string.GetHashCode, which is randomized per process.
    private int Slot(string word)
    {
        uint hash = 2166136261u ^ (uint)seed;
        foreach (char c in word)
        {
            hash = (hash ^ c) * 16777619u;
        }
        return (int)(hash % Dimensions);
    }

    [GeneratedRegex("[a-z0-9]+(?:-[a-z0-9]+)*")]
    private static partial Regex WordPattern();

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }
}
