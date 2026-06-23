// ============================================================================
// Exercise 01 — Similarity Explorer (Beginner)
// User enters two sentences, app generates embeddings and shows cosine
// similarity with interpretation. FULLY STANDALONE.
// ============================================================================

using System.ClientModel;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

using IHost host = builder.Build();
IConfiguration config = host.Services.GetRequiredService<IConfiguration>();
IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator = CreateEmbeddingGenerator(config);

Console.WriteLine("=== Exercise: Similarity Explorer ===\n");
Console.WriteLine("Enter two sentences to compare their semantic similarity.\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// Demo pairs first
(string, string)[] demoPairs =
[
    ("The cat sat on the mat.", "A kitten was resting on the rug."),
    ("The cat sat on the mat.", "Stock prices rose sharply today."),
    ("I love programming in C#.", "C# is my favorite language for coding."),
    ("The weather is sunny today.", "It is raining heavily outside."),
];

Console.WriteLine("--- Demo Pairs ---\n");

foreach ((string s1, string s2) in demoPairs)
{
    try
    {
        GeneratedEmbeddings<Embedding<float>> embeddings = await embeddingGenerator.GenerateAsync(
            [s1, s2], cancellationToken: cts.Token);

        double similarity = CosineSimilarity(
            embeddings[0].Vector.ToArray(),
            embeddings[1].Vector.ToArray());

        string interpretation = Interpret(similarity);

        Console.WriteLine($"  A: \"{s1}\"");
        Console.WriteLine($"  B: \"{s2}\"");
        Console.WriteLine($"  Similarity: {similarity:F4} — {interpretation}\n");
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("\n[Cancelled]");
        return;
    }
}

// Interactive mode
Console.WriteLine("--- Interactive Mode (type 'quit' to exit) ---\n");

while (true)
{
    Console.Write("Sentence A: ");
    string? a = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(a) || a.Equals("quit", StringComparison.OrdinalIgnoreCase)) break;

    Console.Write("Sentence B: ");
    string? b = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(b) || b.Equals("quit", StringComparison.OrdinalIgnoreCase)) break;

    try
    {
        GeneratedEmbeddings<Embedding<float>> embeddings = await embeddingGenerator.GenerateAsync(
            [a, b], cancellationToken: cts.Token);

        double similarity = CosineSimilarity(
            embeddings[0].Vector.ToArray(),
            embeddings[1].Vector.ToArray());

        Console.WriteLine($"  Similarity: {similarity:F4} — {Interpret(similarity)}\n");
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("\n[Cancelled]");
        break;
    }
}

Console.WriteLine("=== Exercise complete! ===");

// ---------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------

/// <summary>
/// Computes cosine similarity between two vectors.
/// </summary>
// In production, prefer System.Numerics.Tensors.TensorPrimitives.CosineSimilarity(a, b) — SIMD-accelerated.
static double CosineSimilarity(float[] a, float[] b)
{
    double dot = 0, magA = 0, magB = 0;
    for (int i = 0; i < a.Length; i++)
    {
        dot += a[i] * (double)b[i];
        magA += a[i] * (double)a[i];
        magB += b[i] * (double)b[i];
    }

    double denominator = Math.Sqrt(magA) * Math.Sqrt(magB);
    return denominator == 0 ? 0 : dot / denominator;
}

/// <summary>
/// Interprets a cosine similarity score.
/// </summary>
static string Interpret(double similarity) => similarity switch
{
    >= 0.9 => "Very similar (nearly identical meaning)",
    >= 0.7 => "Similar (related concepts)",
    >= 0.5 => "Somewhat similar (loose connection)",
    >= 0.3 => "Not very similar (different topics)",
    _ => "Not similar (unrelated)"
};

/// <summary>
/// Creates an embedding generator from configuration.
/// </summary>
static IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(IConfiguration configuration)
{
    string provider = configuration["LlmProvider:Provider"]?.ToLowerInvariant() ?? "github";

    return provider switch
    {
        "azure" => new AzureOpenAIClient(
                new Uri(configuration["LlmProvider:AzureOpenAI:Endpoint"] ?? throw new InvalidOperationException("Missing endpoint")),
                new DefaultAzureCredential())
            .GetEmbeddingClient(configuration["LlmProvider:AzureOpenAI:EmbeddingModel"] ?? "text-embedding-3-small")
            .AsIEmbeddingGenerator(),

        "github" => new OpenAI.OpenAIClient(
                new ApiKeyCredential(configuration["LlmProvider:GitHub:Token"] ?? throw new InvalidOperationException("Missing token")),
                new OpenAI.OpenAIClientOptions { Endpoint = new Uri(configuration["LlmProvider:GitHub:Endpoint"] ?? "https://models.github.ai/inference") })
            .GetEmbeddingClient(configuration["LlmProvider:GitHub:EmbeddingModel"] ?? "text-embedding-3-small")
            .AsIEmbeddingGenerator(),

        "openai" => new OpenAI.OpenAIClient(
                configuration["LlmProvider:OpenAI:ApiKey"] ?? throw new InvalidOperationException("Missing key"))
            .GetEmbeddingClient(configuration["LlmProvider:OpenAI:EmbeddingModel"] ?? "text-embedding-3-small")
            .AsIEmbeddingGenerator(),

        _ => throw new InvalidOperationException($"Embedding not supported for provider: '{provider}'. Use azure, openai, or github.")
    };
}
