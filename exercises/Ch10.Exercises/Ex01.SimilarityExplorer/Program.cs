// ============================================================================
// Exercise 01 — Similarity Explorer (Basic)
// Embeds a hard-coded corpus of 20 sentences (one batched call), then embeds
// each query and shows the TOP-3 most similar sentences with their scores,
// using TensorPrimitives.CosineSimilarity. FULLY STANDALONE.
// ============================================================================

using System.ClientModel;
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
IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator = CreateEmbeddingGenerator(config);

Console.WriteLine("=== Exercise: Similarity Explorer ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// ---------------------------------------------------------------
// Step 1: A small hard-coded corpus of 20 sentences, varied topics.
// ---------------------------------------------------------------
string[] corpus =
[
    "The cat sat on the mat.",                                    //  0: animals
    "A kitten was resting on the rug.",                           //  1: animals
    "The stock market crashed yesterday.",                        //  2: finance
    "Financial markets experienced a sharp decline.",             //  3: finance
    "I love programming in C#.",                                  //  4: programming
    "C# is my favorite programming language.",                    //  5: programming
    "The weather is sunny and warm today.",                       //  6: weather
    "It's a beautiful day outside with clear skies.",             //  7: weather
    "Machine learning models need large datasets.",               //  8: AI/ML
    "Neural networks require significant training data.",         //  9: AI/ML
    "She baked a loaf of sourdough bread this morning.",          // 10: cooking
    "The recipe calls for two cups of flour and a pinch of salt.",// 11: cooking
    "The home team won the championship in overtime.",            // 12: sports
    "The striker scored a stunning goal in the final minute.",    // 13: sports
    "We hiked to the summit and watched the sunrise.",            // 14: travel
    "Their road trip covered five national parks in two weeks.",  // 15: travel
    "The orchestra performed a moving rendition of the symphony.",// 16: music
    "He practices jazz piano for an hour every evening.",         // 17: music
    "The telescope captured images of a distant galaxy.",         // 18: space
    "Astronomers discovered a new exoplanet orbiting a nearby star." // 19: space
];

// ---------------------------------------------------------------
// Step 2: Embed the whole corpus in ONE batched call.
// Batching amortizes the request overhead — one round-trip, not twenty.
// ---------------------------------------------------------------
Console.WriteLine($"Embedding {corpus.Length} corpus sentences (one batched call)...");

GeneratedEmbeddings<Embedding<float>> corpusEmbeddings;
try
{
    corpusEmbeddings = await embeddingGenerator.GenerateAsync(corpus, cancellationToken: cts.Token);
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n[Cancelled]");
    return;
}

Console.WriteLine($"Done ({corpusEmbeddings[0].Vector.Length} dimensions each).\n");

// ---------------------------------------------------------------
// Step 3: Sample queries — top-3 most similar sentences with scores.
// ---------------------------------------------------------------
string[] sampleQueries =
[
    "My kitten loves sleeping on soft furniture.",
    "How did the markets perform today?",
    "Training an AI model takes a lot of data."
];

Console.WriteLine("--- Sample Queries ---\n");

foreach (string query in sampleQueries)
{
    try
    {
        await ShowTopMatchesAsync(query);
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("\n[Cancelled]");
        return;
    }
}

// ---------------------------------------------------------------
// Step 4: Interactive mode — the user's query from the console.
// ---------------------------------------------------------------
Console.WriteLine("--- Interactive Mode (type 'quit' to exit) ---\n");

while (true)
{
    Console.Write("Query: ");
    string? query = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(query) || query.Equals("quit", StringComparison.OrdinalIgnoreCase))
    {
        break;
    }

    try
    {
        await ShowTopMatchesAsync(query);
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
/// Embeds the query and prints the three most similar corpus sentences,
/// ranked by cosine similarity (SIMD-accelerated TensorPrimitives).
/// </summary>
async Task ShowTopMatchesAsync(string query)
{
    Embedding<float> queryEmbedding =
        await embeddingGenerator.GenerateAsync(query, cancellationToken: cts.Token);

    var top3 = corpus
        .Select((text, i) => (
            Text: text,
            Score: TensorPrimitives.CosineSimilarity(
                corpusEmbeddings[i].Vector.Span,
                queryEmbedding.Vector.Span)))
        .OrderByDescending(m => m.Score)
        .Take(3)
        .ToList();

    Console.WriteLine($"Query: \"{query}\"");
    for (int rank = 0; rank < top3.Count; rank++)
    {
        Console.WriteLine($"  {rank + 1}. {top3[rank].Score:F4}  \"{top3[rank].Text}\"");
    }

    Console.WriteLine();
}

/// <summary>
/// Creates an embedding generator from configuration.
/// </summary>
static IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(IConfiguration configuration)
{
    string provider = configuration["LlmProvider:Provider"]?.ToLowerInvariant() ?? "ollama";

    return provider switch
    {
        "azure" => new AzureOpenAIClient(
                new Uri(configuration["LlmProvider:AzureOpenAI:Endpoint"] ?? throw new InvalidOperationException("Missing endpoint")),
                new DefaultAzureCredential())
            .GetEmbeddingClient(configuration["LlmProvider:AzureOpenAI:EmbeddingModel"] ?? "text-embedding-3-small")
            .AsIEmbeddingGenerator(),

        "openai" => new OpenAI.OpenAIClient(
                configuration["LlmProvider:OpenAI:ApiKey"] ?? throw new InvalidOperationException("Missing key"))
            .GetEmbeddingClient(configuration["LlmProvider:OpenAI:EmbeddingModel"] ?? "text-embedding-3-small")
            .AsIEmbeddingGenerator(),

        // The offline default: OllamaApiClient implements IEmbeddingGenerator
        // directly, and nomic-embed-text is the local embedding model the
        // chapter's RAG pipeline uses.
        "ollama" => new OllamaSharp.OllamaApiClient(
            new Uri(configuration["LlmProvider:Ollama:Endpoint"] ?? "http://localhost:11434"),
            configuration["LlmProvider:Ollama:EmbeddingModel"] ?? "nomic-embed-text"),

        _ => throw new InvalidOperationException($"Embedding not supported for provider: '{provider}'")
    };
}
