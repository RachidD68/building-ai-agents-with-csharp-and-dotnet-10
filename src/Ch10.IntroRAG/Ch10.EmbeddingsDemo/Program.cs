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

    "github" => new OpenAI.OpenAIClient(
            new System.ClientModel.ApiKeyCredential(config.GetRequiredValue("LlmProvider:GitHub:Token")),
            new OpenAI.OpenAIClientOptions
            {
                Endpoint = new Uri(config["LlmProvider:GitHub:Endpoint"] ?? "https://models.github.ai/inference")
            })
        .GetEmbeddingClient(config["LlmProvider:GitHub:EmbeddingModel"] ?? "text-embedding-3-small")
        .AsIEmbeddingGenerator(),

    _ => throw new InvalidOperationException(
        $"Unsupported provider '{provider}' for embeddings. Use azure, openai, or github.")
};

Console.WriteLine($"Embedding provider: {provider}\n");

// ---------------------------------------------------------------
// Step 2: Define sample sentences with known semantic relationships
// ---------------------------------------------------------------
string[] sentences =
[
    "The cat sat on the mat.",                           // 0: Animals
    "A kitten was resting on the rug.",                  // 1: Animals (similar to 0)
    "The stock market crashed yesterday.",               // 2: Finance
    "Financial markets experienced a sharp decline.",    // 3: Finance (similar to 2)
    "I love programming in C#.",                         // 4: Programming
    "C# is my favorite programming language.",           // 5: Programming (similar to 4)
    "The weather is sunny and warm today.",              // 6: Weather
    "It's a beautiful day outside with clear skies.",    // 7: Weather (similar to 6)
    "Machine learning models need large datasets.",      // 8: AI/ML
    "Neural networks require significant training data." // 9: AI/ML (similar to 8)
];

Console.WriteLine("Sentences:");
for (int i = 0; i < sentences.Length; i++)
{
    Console.WriteLine($"  [{i}] {sentences[i]}");
}

Console.WriteLine();

// ---------------------------------------------------------------
// Step 3: Generate embeddings for all sentences
// ---------------------------------------------------------------
Console.WriteLine("Generating embeddings...");

GeneratedEmbeddings<Embedding<float>> embeddings = await embeddingGenerator.GenerateAsync(
    sentences,
    cancellationToken: cts.Token);

Console.WriteLine($"Generated {embeddings.Count} embeddings " +
                  $"({embeddings[0].Vector.Length} dimensions each)\n");

// ---------------------------------------------------------------
// Step 4: Calculate and display cosine similarity matrix
// ---------------------------------------------------------------
Console.WriteLine("Cosine Similarity Matrix:");
Console.WriteLine(new string('=', 70));

// Header row
Console.Write("     ");
for (int i = 0; i < sentences.Length; i++)
{
    Console.Write($"  [{i,2}]");
}
Console.WriteLine();

for (int i = 0; i < sentences.Length; i++)
{
    Console.Write($"[{i,2}] ");
    for (int j = 0; j < sentences.Length; j++)
    {
        float similarity = TensorPrimitives.CosineSimilarity(
            embeddings[i].Vector.Span,
            embeddings[j].Vector.Span);

        // Color-code: green for high similarity, yellow for medium, default for low
        if (i != j && similarity > 0.85f)
        {
            Console.ForegroundColor = ConsoleColor.Green;
        }
        else if (i != j && similarity > 0.75f)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
        }

        Console.Write($" {similarity,5:F2} ");
        Console.ResetColor();
    }

    Console.WriteLine();
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
