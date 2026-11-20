using Ch11.AuthorizedSearch;

namespace Ch11.HybridRetrieval;

public sealed record Chunk(string Id, string Text, float[] Vector);

public sealed record Ranked(string Id, double Score);

/// <summary>BM25 over the words of each chunk: exact names, identifiers and rare terms.</summary>
public sealed class LexicalIndex(IReadOnlyList<Chunk> chunks, double k1 = 1.2, double b = 0.75)
{
    private readonly List<(string Id, string[] Words)> _docs =
        [.. chunks.Select(c => (c.Id, HashingEmbedder.Words(c.Text).ToArray()))];

    public IReadOnlyList<Ranked> Search(string query, int top)
    {
        double avgLength = _docs.Average(d => d.Words.Length);
        string[] terms = [.. HashingEmbedder.Words(query).Distinct()];
        List<Ranked> scored = [];
        foreach ((string id, string[] words) in _docs)
        {
            double score = 0;
            foreach (string term in terms)
            {
                int tf = words.Count(w => w == term);
                if (tf == 0) continue;
                int df = _docs.Count(d => d.Words.Contains(term));
                double idf = Math.Log(1 + (_docs.Count - df + 0.5) / (df + 0.5));
                score += idf * tf * (k1 + 1) / (tf + k1 * (1 - b + b * words.Length / avgLength));
            }
            if (score > 0) scored.Add(new Ranked(id, score));        // no shared word, no candidate
        }
        return [.. scored.OrderByDescending(r => r.Score).ThenBy(r => r.Id, StringComparer.Ordinal).Take(top)];
    }
}

/// <summary>Cosine similarity over the fixed vectors: wording may differ, meaning is close.</summary>
public sealed class VectorIndex(IReadOnlyList<Chunk> chunks)
{
    public IReadOnlyList<Ranked> Search(float[] query, int top) =>
        [.. chunks.Select(c => new Ranked(c.Id, Cosine(query, c.Vector)))
                  .OrderByDescending(r => r.Score).ThenBy(r => r.Id, StringComparer.Ordinal).Take(top)];

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return na == 0 || nb == 0 ? 0 : dot / Math.Sqrt(na * nb);
    }
}

/// <summary>
/// Reciprocal rank fusion: each list contributes 1 / (k + rank). It reads ranks,
/// not scores, because a BM25 score and a cosine have no common scale.
/// </summary>
public static class Fusion
{
    public static IReadOnlyList<Ranked> ReciprocalRank(IEnumerable<IReadOnlyList<Ranked>> lists, int k = 60) =>
        [.. lists.SelectMany(list => list.Select((r, i) => (r.Id, Score: 1.0 / (k + i + 1))))
                 .GroupBy(x => x.Id)
                 .Select(g => new Ranked(g.Key, g.Sum(x => x.Score)))
                 .OrderByDescending(r => r.Score).ThenBy(r => r.Id, StringComparer.Ordinal)];
}

/// <summary>A reranker sees authorized candidates only, and may reorder, never add.</summary>
public interface IReranker
{
    IReadOnlyList<Ranked> Rerank(string query, IReadOnlyList<Ranked> candidates, IReadOnlyDictionary<string, Chunk> chunks);
}

/// <summary>
/// The stand-in for a hosted reranker: more distinct query words in the chunk
/// first, fused order second. Deterministic, so a test can pin its effect.
/// </summary>
public sealed class TermOverlapReranker : IReranker
{
    public IReadOnlyList<Ranked> Rerank(string query, IReadOnlyList<Ranked> candidates, IReadOnlyDictionary<string, Chunk> chunks)
    {
        HashSet<string> terms = [.. HashingEmbedder.Words(query)];
        return [.. candidates
            .Select((r, i) => (r, Overlap: HashingEmbedder.Words(chunks[r.Id].Text).Distinct().Count(terms.Contains), Position: i))
            .OrderByDescending(x => x.Overlap).ThenBy(x => x.Position)
            .Select(x => x.r)];
    }
}

public static class Metrics
{
    /// <summary>Did at least one relevant item reach the top k?</summary>
    public static double HitRate(IEnumerable<(IReadOnlyList<string> Retrieved, IReadOnlyCollection<string> Relevant)> runs, int k) =>
        runs.Average(r => r.Retrieved.Take(k).Any(r.Relevant.Contains) ? 1.0 : 0.0);

    /// <summary>What fraction of the relevant items reached the top k?</summary>
    public static double Recall(IEnumerable<(IReadOnlyList<string> Retrieved, IReadOnlyCollection<string> Relevant)> runs, int k) =>
        runs.Average(r => r.Retrieved.Take(k).Count(r.Relevant.Contains) / (double)r.Relevant.Count);
}
