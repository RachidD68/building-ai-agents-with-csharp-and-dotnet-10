using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;

namespace Ex02.DocumentQA;

/// <summary>A document with its embedding vector.</summary>
public sealed class DocumentChunk
{
    /// <summary>
    /// Vector size, part of the collection schema. It must equal the configured
    /// EmbeddingDimensions and the size the model returns; both are checked at startup.
    /// 768 = nomic-embed-text, the local default.
    /// </summary>
    public const int Dimensions = 768;

    [VectorStoreKey] public string Id { get; set; } = string.Empty;
    [VectorStoreData] public string Content { get; set; } = string.Empty;
    [VectorStoreVector(Dimensions, DistanceFunction = DistanceFunction.CosineSimilarity)]
    public ReadOnlyMemory<float> Vector { get; set; }
}

/// <summary>The answer, the ids it was given, and whether the code abstained.</summary>
public sealed record QaAnswer(string Text, IReadOnlyList<string> SourceIds, bool Abstained)
{
    /// <summary>True when the answer cites at least one given id and no other id.</summary>
    public bool CitesItsSources(IEnumerable<string> knownIds)
    {
        string[] cited = [.. Regex.Matches(Text, @"\[([a-z0-9-]+)\]").Select(m => m.Groups[1].Value)
            .Where(knownIds.Contains).Distinct()];
        return cited.Length > 0 && cited.All(SourceIds.Contains);
    }
}

/// <summary>Retrieve, then answer with an agent. Nothing retrieved: abstain without calling the model.</summary>
public sealed class DocumentQa(
    IEmbeddingGenerator<string, Embedding<float>> embeddings,
    VectorStoreCollection<string, DocumentChunk> collection,
    AIAgent agent,
    double scoreThreshold,
    int top = 2)
{
    public const string Abstention = "That is not in the NovaTech documents I have.";

    public const string Instructions = """
        You are a helpful assistant for NovaTech Inc. Answer using ONLY the provided context.
        Cite the id of every document you use in square brackets, e.g. [about].
        If the context does not contain the answer, say so.
        """;

    /// <summary>Fails when the configured size, the declared size and the model's size are not all equal.</summary>
    public static async Task CheckDimensionsAsync(
        int configured, IEmbeddingGenerator<string, Embedding<float>> embeddings, CancellationToken cancellationToken = default)
    {
        if (configured != DocumentChunk.Dimensions)
            throw new InvalidOperationException(
                $"EmbeddingDimensions is {configured} in appsettings.json, but DocumentChunk declares {DocumentChunk.Dimensions}.");
        int actual = (await embeddings.GenerateAsync("dimension probe", cancellationToken: cancellationToken)).Vector.Length;
        if (actual != DocumentChunk.Dimensions)
            throw new InvalidOperationException(
                $"The embedding model returned {actual} dimensions, but DocumentChunk declares {DocumentChunk.Dimensions}.");
    }

    public static async Task IngestAsync(
        IEmbeddingGenerator<string, Embedding<float>> embeddings,
        VectorStoreCollection<string, DocumentChunk> collection,
        IReadOnlyDictionary<string, string> documents, CancellationToken cancellationToken = default)
    {
        await collection.EnsureCollectionExistsAsync(cancellationToken);
        GeneratedEmbeddings<Embedding<float>> vectors =
            await embeddings.GenerateAsync(documents.Values, cancellationToken: cancellationToken);
        await collection.UpsertAsync(
            documents.Keys.Select((id, i) => new DocumentChunk { Id = id, Content = documents[id], Vector = vectors[i].Vector }),
            cancellationToken);
    }

    public async Task<QaAnswer> AskAsync(string question, CancellationToken cancellationToken = default)
    {
        Embedding<float> query = await embeddings.GenerateAsync(question, cancellationToken: cancellationToken);
        List<DocumentChunk> hits = [];
        await foreach (VectorSearchResult<DocumentChunk> r in collection.SearchAsync(query.Vector, top, cancellationToken: cancellationToken))
        {
            if ((r.Score ?? 0) >= scoreThreshold) hits.Add(r.Record);
        }

        if (hits.Count == 0) return new QaAnswer(Abstention, [], Abstained: true);

        string context = string.Join("\n\n", hits.Select(h => $"[{h.Id}]: {h.Content}"));
        AgentResponse response = await agent.RunAsync(
            $"Context:\n{context}\n\nQuestion: {question}",
            await agent.CreateSessionAsync(cancellationToken),
            cancellationToken: cancellationToken);
        return new QaAnswer(response.Text, [.. hits.Select(h => h.Id)], Abstained: false);
    }
}
