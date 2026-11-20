// ============================================================================
// Exercise 02 — Document Q&A (Intermediate)
// RAG pipeline: embed documents, store in a vector collection, retrieve, and
// answer with an agent that cites document ids. Nothing retrieved above the
// threshold: the code abstains without calling the model. Five sample docs
// about a fictional company. FULLY STANDALONE.
//
//   dotnet run                  against the configured models (Ollama by default)
//   dotnet run -- --self-test   fake model + fake embeddings, no network; RESULT n/n
// ============================================================================

using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using CommunityToolkit.VectorData.InMemory;
using Ex02.DocumentQA;

if (args.Contains("--self-test")) return await SelfTest.RunAsync();

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
using IHost host = builder.Build();
IConfiguration config = host.Services.GetRequiredService<IConfiguration>();

Console.WriteLine("=== Exercise: Document Q&A (RAG Pipeline) ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator = CreateEmbeddingGenerator(config);
IChatClient chatClient = CreateChatClient(config);
string provider = config["LlmProvider:Provider"]?.ToLowerInvariant() ?? "ollama";
string section = provider switch { "azure" => "AzureOpenAI", "openai" => "OpenAI", _ => "Ollama" };

// The configured size, the declared size and the model's size must agree.
await DocumentQa.CheckDimensionsAsync(
    config.GetValue<int?>($"LlmProvider:{section}:EmbeddingDimensions") ?? 0, embeddingGenerator, cts.Token);

InMemoryCollection<string, DocumentChunk> collection = new("documents");
await DocumentQa.IngestAsync(embeddingGenerator, collection, SampleDocuments.All, cts.Token);
Console.WriteLine($"Indexed {SampleDocuments.All.Count} documents ({DocumentChunk.Dimensions} dimensions).\n");

ChatClientAgent agent = new(chatClient, instructions: DocumentQa.Instructions, name: "NovaTechQA");
double threshold = config.GetValue<double?>("DocumentQa:ScoreThreshold") ?? 0.55;
DocumentQa qa = new(embeddingGenerator, collection, agent, threshold);

string[] sampleQuestions =
[
    "Who founded NovaTech and when?",
    "What programming languages does CodePilot support?",
    "How much revenue did NovaTech make in 2025?",
    "What is the dress code at the NovaTech offices?",   // not in the documents
];

foreach (string question in sampleQuestions)
{
    Console.WriteLine($"Q: {question}");
    try
    {
        QaAnswer answer = await qa.AskAsync(question, cts.Token);
        Console.WriteLine($"A: {answer.Text}");
        Console.WriteLine(answer.Abstained
            ? "   (abstained: nothing retrieved above the threshold, model not called)\n"
            : $"   given [{string.Join(", ", answer.SourceIds)}]; cites them: {answer.CitesItsSources(SampleDocuments.All.Keys)}\n");
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("\n[Cancelled]");
        return 130;
    }
}

Console.WriteLine("=== Exercise complete! ===");
return 0;

// ---------------------------------------------------------------
// Provider factories
// ---------------------------------------------------------------

/// <summary>Creates an IChatClient from configuration.</summary>
static IChatClient CreateChatClient(IConfiguration configuration)
{
    string provider = configuration["LlmProvider:Provider"]?.ToLowerInvariant() ?? "ollama";
    return provider switch
    {
        "azure" => new AzureOpenAIClient(
                new Uri(configuration["LlmProvider:AzureOpenAI:Endpoint"] ?? throw new InvalidOperationException("Missing endpoint")),
                new DefaultAzureCredential())
            .GetChatClient(configuration["LlmProvider:AzureOpenAI:Model"] ?? "gpt-5.6-luna").AsIChatClient(),
        "openai" => new OpenAI.OpenAIClient(configuration["LlmProvider:OpenAI:ApiKey"] ?? throw new InvalidOperationException("Missing key"))
            .GetChatClient(configuration["LlmProvider:OpenAI:Model"] ?? "gpt-5.6-luna").AsIChatClient(),
        "ollama" => new OllamaSharp.OllamaApiClient(
            new Uri(configuration["LlmProvider:Ollama:Endpoint"] ?? "http://localhost:11434"),
            configuration["LlmProvider:Ollama:Model"] ?? "llama3.2"),
        _ => throw new InvalidOperationException($"Unknown provider: '{provider}'")
    };
}

/// <summary>Creates an embedding generator from configuration.</summary>
static IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(IConfiguration configuration)
{
    string provider = configuration["LlmProvider:Provider"]?.ToLowerInvariant() ?? "ollama";
    return provider switch
    {
        "azure" => new AzureOpenAIClient(
                new Uri(configuration["LlmProvider:AzureOpenAI:Endpoint"] ?? throw new InvalidOperationException("Missing endpoint")),
                new DefaultAzureCredential())
            .GetEmbeddingClient(configuration["LlmProvider:AzureOpenAI:EmbeddingModel"] ?? "text-embedding-3-small").AsIEmbeddingGenerator(),
        "openai" => new OpenAI.OpenAIClient(configuration["LlmProvider:OpenAI:ApiKey"] ?? throw new InvalidOperationException("Missing key"))
            .GetEmbeddingClient(configuration["LlmProvider:OpenAI:EmbeddingModel"] ?? "text-embedding-3-small").AsIEmbeddingGenerator(),
        // The offline default: OllamaApiClient implements IEmbeddingGenerator directly.
        "ollama" => new OllamaSharp.OllamaApiClient(
            new Uri(configuration["LlmProvider:Ollama:Endpoint"] ?? "http://localhost:11434"),
            configuration["LlmProvider:Ollama:EmbeddingModel"] ?? "nomic-embed-text"),
        _ => throw new InvalidOperationException($"Embedding not supported for provider: '{provider}'")
    };
}

/// <summary>Five short documents about the fictional NovaTech Inc.</summary>
static class SampleDocuments
{
    public static readonly IReadOnlyDictionary<string, string> All = new Dictionary<string, string>
    {
        ["about"] = "NovaTech Inc. was founded in 2020 in Seattle by CEO Sarah Chen. The company specializes in AI-powered developer tools and has grown to 250 employees across three offices in Seattle, London, and Tokyo.",
        ["products"] = "NovaTech's flagship product is CodePilot, an AI pair-programming assistant that supports C#, Python, and TypeScript. They also offer DataLens for data analytics and CloudForge for infrastructure automation. CodePilot has over 50,000 active users.",
        ["culture"] = "NovaTech promotes a remote-first work culture with quarterly in-person gatherings. The company offers unlimited PTO, learning stipends of $3,000 per year, and equity grants for all employees. They emphasize open-source contributions.",
        ["engineering"] = "The engineering team at NovaTech uses .NET 10 and Azure for backend services, React for frontends, and Kubernetes for orchestration. They practice trunk-based development with automated CI/CD pipelines deploying 20 times per day.",
        ["financials"] = "NovaTech reported $45M in annual recurring revenue for 2025, up 120% from the previous year. They closed a Series B round of $80M led by TechVentures Capital. The company expects to reach profitability by Q3 2026.",
    };
}
