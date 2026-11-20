using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;

namespace Ch10.FaqAgent;

/// <summary>
/// The ingest path of the FAQ knowledge base: check the embedding model, chunk
/// the Markdown handbook by paragraph, embed every chunk in one batch, store.
/// </summary>
public static class FaqIngest
{
    /// <summary>Paragraphs shorter than this (headings, stray lines) are not indexed.</summary>
    public const int MinChunkLength = 30;

    /// <summary>
    /// Embeds one probe text and fails if the vector size differs from the
    /// collection schema (<see cref="FaqChunk.Dimensions"/>). Run it before reading
    /// a single document, so a wrong model fails at startup, not halfway through.
    /// </summary>
    public static async Task CheckModelAsync(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        CancellationToken cancellationToken = default)
    {
        Embedding<float> probe = await generator.GenerateAsync(
            "dimension probe", cancellationToken: cancellationToken);
        FaqChunk.CheckDimensions(probe.Vector);
    }

    /// <summary>
    /// Splits one Markdown document into paragraph chunks. The chunk id is
    /// "source-paragraphIndex", so it is stable for an unchanged document and
    /// an annotated evaluation set can name it.
    /// </summary>
    public static IReadOnlyList<FaqChunk> Chunk(string source, string markdown)
    {
        string topic = markdown.Split('\n', 2)[0].TrimStart('#', ' ').Trim();
        string[] paragraphs = markdown.Split(
            ["\r\n\r\n", "\n\n"],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        List<FaqChunk> chunks = [];
        for (int i = 0; i < paragraphs.Length; i++)
        {
            if (paragraphs[i].Length < MinChunkLength) continue;
            chunks.Add(new FaqChunk
            {
                Id = $"{source}-{i}",
                Content = paragraphs[i],
                Source = source,
                Topic = topic,
            });
        }
        return chunks;
    }

    /// <summary>Reads and chunks every .md file of a folder, in file-name order.</summary>
    public static async Task<IReadOnlyList<FaqChunk>> LoadAsync(
        string docsPath, CancellationToken cancellationToken = default)
    {
        List<FaqChunk> chunks = [];
        foreach (string file in Directory.GetFiles(docsPath, "*.md").Order(StringComparer.Ordinal))
        {
            string markdown = await File.ReadAllTextAsync(file, cancellationToken);
            chunks.AddRange(Chunk(Path.GetFileNameWithoutExtension(file), markdown));
        }
        return chunks;
    }

    /// <summary>
    /// Embeds all chunks in one batched call, checks every vector, then stores
    /// them. Nothing is stored unless every vector has the schema's size.
    /// </summary>
    public static async Task<int> IngestAsync(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        VectorStoreCollection<string, FaqChunk> collection,
        IReadOnlyList<FaqChunk> chunks,
        CancellationToken cancellationToken = default)
    {
        await collection.EnsureCollectionExistsAsync(cancellationToken);
        GeneratedEmbeddings<Embedding<float>> vectors = await generator.GenerateAsync(
            chunks.Select(c => c.Content), cancellationToken: cancellationToken);

        for (int i = 0; i < chunks.Count; i++)
        {
            chunks[i].Embedding = FaqChunk.CheckDimensions(vectors[i].Vector);
        }

        await collection.UpsertAsync(chunks, cancellationToken);
        return chunks.Count;
    }
}
