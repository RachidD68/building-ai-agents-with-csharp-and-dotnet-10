// ============================================================================
// Chapter 10 — Introduction to RAG (Embeddings Demo)
// Generates embeddings for sample sentences and displays a cosine similarity
// matrix, showing that semantically similar sentences score higher.
//
// Key concepts:
//   - IEmbeddingGenerator<string, Embedding<float>> abstraction
//   - Generating embeddings via Azure OpenAI / OpenAI
//   - Cosine similarity calculation
//   - Visualizing semantic relationships in the console
// ============================================================================

using System.Numerics.Tensors;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

using IHost host = builder.Build();
IConfiguration config = host.Services.GetRequiredService<IConfiguration>();

Console.WriteLine("=== Chapter 10: Embeddings Demo — Semantic Similarity ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// ---------------------------------------------------------------
// Step 1: Create an embedding generator
// ---------------------------------------------------------------
// The IEmbeddingGenerator<string, Embedding<float>> abstraction lets us
// swap providers without changing downstream code.
string provider = config["LlmProvider:Provider"] ?? "azure";
IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator = provider.ToLowerInvariant() switch
{
    "azure" => new AzureOpenAIClient(
            new Uri(config.GetRequiredValue("LlmProvider:AzureOpenAI:Endpoint")),
            new DefaultAzureCredential())
        .GetEmbeddingClient(config["LlmProvider:AzureOpenAI:EmbeddingModel"] ?? "text-embedding-3-small")
        .AsIEmbeddingGenerator(),

    "openai" => new OpenAI.OpenAIClient(config.GetRequiredValue("LlmProvider:OpenAI:ApiKey"))
        .GetEmbeddingClient(config["LlmProvider:OpenAI:EmbeddingModel"] ?? "text-embedding-3-small")
        .AsIEmbeddingGenerator(),

    // The offline default: OllamaApiClient implements IEmbeddingGenerator
    // directly, with nomic-embed-text as the local embedding model.
    "ollama" => new OllamaSharp.OllamaApiClient(
        new Uri(config["LlmProvider:Ollama:Endpoint"] ?? "http://localhost:11434"),
        config["LlmProvider:Ollama:EmbeddingModel"] ?? "nomic-embed-text"),

    _ => throw new InvalidOperationException(
        $"Unsupported provider '{provider}' for embeddings. Use azure, openai, or ollama.")
};

Console.WriteLine($"Embedding provider: {provider}\n");

// ---------------------------------------------------------------
// Step 2: Define sample sentences with known semantic relationships
// ---------------------------------------------------------------
// Two sentences about cats, two about .NET, and one outlier about pasta.
// The topic labels drive the header row of the similarity matrix below.
string[] sentences =
[
    "Cats are small, carnivorous mammals that are often kept as pets.",
    "Felines make affectionate companions and are popular household animals.",
    "The C# programming language was developed by Microsoft in the early 2000s.",
    "Dotnet is a cross-platform software framework for building applications.",
    "Pasta is a staple food of traditional Italian cuisine."
];

string[] topics = ["Cats", "Felines", "C#", "Dotnet", "Pasta"];

// ---------------------------------------------------------------
// Step 3: Generate embeddings for all sentences in one batched call
// ---------------------------------------------------------------
GeneratedEmbeddings<Embedding<float>> embeddings = await embeddingGenerator.GenerateAsync(
    sentences,
    cancellationToken: cts.Token);

Console.WriteLine($"Generated {embeddings.Count} embeddings " +
                  $"({embeddings[0].Vector.Length} dimensions each)\n");

// ---------------------------------------------------------------
// Step 4: Calculate and display the 5x5 cosine similarity matrix
// ---------------------------------------------------------------
// Header row: one topic label per sentence, aligned to the value columns.
string header = new string(' ', 25) + string.Concat(topics.Select(t => $"{t,-11}"));
Console.WriteLine(header.TrimEnd());

for (int i = 0; i < sentences.Length; i++)
{
    // Row label: the start of the sentence, e.g. "Cats are small...".
    string row = $"{RowLabel(sentences[i]),-25}";

    for (int j = 0; j < sentences.Length; j++)
    {
        float similarity = TensorPrimitives.CosineSimilarity(
            embeddings[i].Vector.Span,
            embeddings[j].Vector.Span);

        row += $"{similarity,-11:F4}";
    }

    Console.WriteLine(row.TrimEnd());
}

// ---------------------------------------------------------------
// Step 5: Show the most similar pairs
// ---------------------------------------------------------------
Console.WriteLine($"\n{new string('=', 70)}");
Console.WriteLine("Most Similar Pairs (excluding self-comparisons):\n");

List<(int I, int J, float Score)> pairs = [];

for (int i = 0; i < sentences.Length; i++)
{
    for (int j = i + 1; j < sentences.Length; j++)
    {
        float score = TensorPrimitives.CosineSimilarity(
            embeddings[i].Vector.Span,
            embeddings[j].Vector.Span);

        pairs.Add((i, j, score));
    }
}

foreach (var (i, j, score) in pairs.OrderByDescending(p => p.Score).Take(5))
{
    Console.WriteLine($"  {score:F4}  [{i}] \"{sentences[i]}\"");
    Console.WriteLine($"         [{j}] \"{sentences[j]}\"\n");
}

Console.WriteLine("Notice: Semantically similar sentences (same topic) have higher scores,");
Console.WriteLine("even though they use different words. This is the power of embeddings.");

Console.WriteLine("\n=== Embeddings Demo complete! ===");

// Truncates a sentence at a word boundary (max 15 chars) to make a compact
// matrix row label, e.g. "Cats are small, carnivorous..." -> "Cats are small...".
static string RowLabel(string sentence)
{
    string label = string.Empty;

    foreach (string word in sentence.Split(' '))
    {
        string candidate = label.Length == 0 ? word : $"{label} {word}";
        if (candidate.Length > 15)
        {
            break;
        }

        label = candidate;
    }

    return $"{label.TrimEnd(',', '.')}...";
}

/// <summary>
/// Extension helper for reading required configuration values.
/// </summary>
static class ConfigExtensions
{
    /// <summary>
    /// Gets a required configuration value or throws with a helpful message.
    /// </summary>
    /// <param name="config">The configuration instance.</param>
    /// <param name="key">The configuration key to read.</param>
    /// <returns>The configuration value.</returns>
    public static string GetRequiredValue(this IConfiguration config, string key)
        => config[key] ?? throw new InvalidOperationException(
            $"Missing configuration '{key}'. Run: dotnet user-secrets set \"{key}\" \"<value>\"");
}
