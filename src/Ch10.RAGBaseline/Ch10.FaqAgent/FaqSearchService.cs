using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;

namespace Ch10.FaqAgent;

/// <summary>One retrieved chunk: its id, its source document, its text and its cosine score.</summary>
public sealed record FaqHit(string ChunkId, string Source, string Text, double Score);

/// <summary>
/// The query path of the FAQ knowledge base: embed the question, run a vector
/// search, keep the hits at or above the score threshold.
/// </summary>
/// <remarks>
/// The service keeps no per-call state: the question arrives as an argument, so
/// one instance can serve concurrent requests. The threshold depends on the
/// corpus and the embedding model; calibrate it on an annotated set
/// (Ch10.RetrievalEval) instead of copying a number.
/// </remarks>
public sealed class FaqSearchService(
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    VectorStoreCollection<string, FaqChunk> collection,
    int topK = 3,
    double scoreThreshold = 0.55,
    Action<string, IReadOnlyList<FaqHit>>? observer = null)
{
    public int TopK => topK;
    public double ScoreThreshold => scoreThreshold;

    /// <summary>The top-K hits at or above the threshold, best first. Empty means "nothing relevant".</summary>
    public async Task<IReadOnlyList<FaqHit>> RetrieveAsync(
        string query, CancellationToken cancellationToken = default)
    {
        Embedding<float> queryEmbedding = await embeddingGenerator.GenerateAsync(
            query, cancellationToken: cancellationToken);

        List<FaqHit> hits = [];
        await foreach (VectorSearchResult<FaqChunk> hit in collection.SearchAsync(
            queryEmbedding.Vector, top: topK, cancellationToken: cancellationToken))
        {
            double score = hit.Score ?? 0;
            if (score < scoreThreshold) continue;
            hits.Add(new FaqHit(hit.Record.Id, hit.Record.Source, hit.Record.Content, score));
        }
        observer?.Invoke(query, hits);   // logging only: the observer sees the exact search input
        return hits;
    }

    /// <summary>
    /// The search delegate handed to <see cref="TextSearchProvider"/>:
    /// <c>(string query, CancellationToken) =&gt; Task&lt;IEnumerable&lt;TextSearchResult&gt;&gt;</c>.
    /// An empty result injects no context, and the agent's instructions abstain.
    /// </summary>
    public async Task<IEnumerable<TextSearchProvider.TextSearchResult>> SearchAsync(
        string query, CancellationToken cancellationToken)
    {
        IReadOnlyList<FaqHit> hits = await RetrieveAsync(query, cancellationToken);
        return hits.Select(h => new TextSearchProvider.TextSearchResult
        {
            Text = h.Text,
            SourceName = h.Source,
            // The local Markdown corpus has no URL; a hosted knowledge base sets one.
            SourceLink = null,
        });
    }
}
