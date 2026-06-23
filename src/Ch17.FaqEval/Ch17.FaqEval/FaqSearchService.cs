using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.InMemory;

namespace Ch17.FaqEval;

/// <summary>
/// The retrieval half of the FAQ agent: it embeds a query, runs a vector search
/// over the FAQ knowledge base, and shapes the hits into the result type that
/// <see cref="TextSearchProvider"/> understands.
/// </summary>
/// <remarks>
/// This is the exact retrieval logic from <c>Ch10.RagPipeline</c> — embed the
/// query, search the store, keep the strong matches — wrapped so the framework's
/// built-in <see cref="TextSearchProvider"/> can call it before every agent run.
/// The provider reads the recent conversation itself, so there is no mutable
/// <c>CurrentQuery</c> to set: the search function simply receives the query text.
/// </remarks>
public sealed class FaqSearchService
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly InMemoryCollection<string, FaqChunk> _collection;
    private readonly int _topK;
    private readonly float _scoreThreshold;

    /// <summary>
    /// Initializes the retrieval service.
    /// </summary>
    /// <param name="embeddingGenerator">Generates the query embedding.</param>
    /// <param name="collection">The vector store collection holding FAQ chunks.</param>
    /// <param name="topK">Maximum number of chunks to return.</param>
    /// <param name="scoreThreshold">
    /// Minimum cosine similarity for a chunk to count as relevant. Weak matches are
    /// dropped, so an out-of-scope question yields no context — and the agent's
    /// "I don't know" instruction does the rest.
    /// </param>
    public FaqSearchService(
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        InMemoryCollection<string, FaqChunk> collection,
        int topK = 3,
        float scoreThreshold = 0.55f)
    {
        _embeddingGenerator = embeddingGenerator;
        _collection = collection;
        _topK = topK;
        _scoreThreshold = scoreThreshold;
    }

    /// <summary>
    /// The search delegate handed to <see cref="TextSearchProvider"/>. Matches the
    /// shape it expects: <c>(string query, CancellationToken) =&gt; Task&lt;IEnumerable&lt;TextSearchResult&gt;&gt;</c>.
    /// </summary>
    public async Task<IEnumerable<TextSearchProvider.TextSearchResult>> SearchAsync(
        string query,
        CancellationToken cancellationToken)
    {
        Embedding<float> queryEmbedding = await _embeddingGenerator.GenerateAsync(
            query,
            cancellationToken: cancellationToken);

        List<TextSearchProvider.TextSearchResult> results = [];

        await foreach (VectorSearchResult<FaqChunk> hit in _collection.SearchAsync(
            queryEmbedding.Vector,
            top: _topK,
            cancellationToken: cancellationToken))
        {
            // Skip weak matches so out-of-scope questions inject no context.
            if (hit.Score is double score && score < _scoreThreshold)
            {
                continue;
            }

            results.Add(new TextSearchProvider.TextSearchResult
            {
                Text = hit.Record.Content,
                SourceName = hit.Record.Source,
                // No public URL for the local Markdown corpus; a hosted KB would
                // set SourceLink here and the provider would render it as a citation.
                SourceLink = null,
            });
        }

        return results;
    }
}
