using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using CommunityToolkit.VectorData.InMemory;

namespace Ch10.FaqAgent.CustomProvider;

/// <summary>
/// The hand-rolled version of what <see cref="TextSearchProvider"/> does under the
/// hood: an <see cref="AIContextProvider"/> that embeds the query, runs a vector
/// search, and injects the hits as extra instructions before each agent call.
/// </summary>
/// <remarks>
/// <para>
/// In the capstone (<c>Ch10.FaqAgent</c>) we use the built-in
/// <see cref="TextSearchProvider"/>. Reach for a custom provider like this one
/// when you need control the built-in does not give you: custom reranking,
/// blending several retrieval sources, or shaping structured/dynamic context.
/// </para>
/// <para>
/// The query comes from the invocation itself: <see cref="AIContextProvider.InvokingContext.AIContext"/>
/// already carries the caller's request messages (filtered by default to external
/// input, so injected history is left out). The provider keeps no per-call state,
/// so one instance can be a DI singleton shared by concurrent requests.
/// </para>
/// </remarks>
public sealed class RagContextProvider : AIContextProvider
{
    private readonly InMemoryCollection<string, FaqChunk> _collection;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly int _topK;
    private readonly float _scoreThreshold;

    /// <summary>
    /// Initializes a new instance with the vector store and embedding generator.
    /// </summary>
    public RagContextProvider(
        InMemoryCollection<string, FaqChunk> collection,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        int topK = 3,
        float scoreThreshold = 0.55f)
    {
        _collection = collection;
        _embeddingGenerator = embeddingGenerator;
        _topK = topK;
        _scoreThreshold = scoreThreshold;
    }

    /// <summary>
    /// Called before each agent invocation. Retrieves relevant FAQ chunks for the
    /// latest user message of this invocation and returns them as additional context.
    /// </summary>
    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken)
    {
        // The user's question for THIS call: the last user message in the request.
        string? query = context.AIContext.Messages?
            .LastOrDefault(m => m.Role == ChatRole.User)?
            .Text;

        if (string.IsNullOrWhiteSpace(query))
        {
            return new AIContext();
        }

        Embedding<float> queryEmbedding = await _embeddingGenerator.GenerateAsync(
            query,
            cancellationToken: cancellationToken);

        List<string> retrievedChunks = [];

        await foreach (VectorSearchResult<FaqChunk> result in _collection.SearchAsync(
            queryEmbedding.Vector,
            top: _topK,
            cancellationToken: cancellationToken))
        {
            if (result.Score is double score && score < _scoreThreshold)
            {
                continue;
            }

            retrievedChunks.Add(
                $"[Source: {result.Record.Source}, Topic: {result.Record.Topic}]\n{result.Record.Content}");
        }

        if (retrievedChunks.Count == 0)
        {
            return new AIContext();
        }

        string contextText = string.Join("\n\n---\n\n", retrievedChunks);

        return new AIContext
        {
            Instructions = $"""
                Use the following retrieved documents to answer the user's question.
                If the answer is not found in the retrieved documents, say "I don't have
                information about that in our knowledge base" and suggest the user contact
                HR or the relevant department.

                Retrieved Documents:
                {contextText}
                """
        };
    }
}
