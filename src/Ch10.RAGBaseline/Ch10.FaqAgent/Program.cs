// ============================================================================
// Contoso FAQ agent: a grounded RAG baseline
// ----------------------------------------------------------------------------
// Ingest path (once):  check the embedding model's dimensions -> chunk the
//                      Markdown handbook by paragraph -> embed -> store.
// Query path (per call): a context provider embeds the question, searches the
//                      store, keeps hits at or above the threshold and adds
//                      them, with their sources, before the model is called.
//                      No hit -> no evidence -> the instructions abstain.
//
//   dotnet run                          TextSearchProvider, search before each call
//   dotnet run -- --provider on-demand  TextSearchProvider as a search tool
//   dotnet run -- --provider custom     FaqContextProvider (stateless, custom)
//   dotnet run -- --self-test           fake model + fake embeddings; RESULT n/n
// ============================================================================

using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using CommunityToolkit.VectorData.InMemory;
using AgenticAI.Common.Extensions;
using Ch10.FaqAgent;

if (args.Contains("--self-test")) return await FaqSelfTest.RunAsync();

int at = Array.IndexOf(args, "--provider");
RetrievalMode mode = (at >= 0 && at + 1 < args.Length ? args[at + 1] : "textsearch") switch
{
    "custom" => RetrievalMode.Custom,
    "on-demand" => RetrievalMode.OnDemand,
    "textsearch" => RetrievalMode.TextSearch,
    string other => throw new ArgumentException($"Unknown --provider '{other}'. Use textsearch, on-demand or custom."),
};

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddAzureOpenAIProvider();   // the azure arm, from AgenticAI.Common.Azure
builder.Services.AddLlmChatClient(builder.Configuration);

using IHost host = builder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();
IConfiguration config = host.Services.GetRequiredService<IConfiguration>();

Console.WriteLine($"=== Contoso FAQ agent ({mode}) ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

IEmbeddingGenerator<string, Embedding<float>> embeddings = CreateEmbeddingGenerator(config);

// Ingest. The dimension check runs first: a model with another vector size
// fails here, before one document is read.
await FaqIngest.CheckModelAsync(embeddings, cts.Token);
IReadOnlyList<FaqChunk> chunks = await FaqIngest.LoadAsync(
    Path.Combine(AppContext.BaseDirectory, "docs"), cts.Token);
InMemoryCollection<string, FaqChunk> collection = new("faq");
await FaqIngest.IngestAsync(embeddings, collection, chunks, cts.Token);
Console.WriteLine($"Indexed {chunks.Count} chunks from {chunks.Select(c => c.Source).Distinct().Count()} documents " +
                  $"({FaqChunk.Dimensions} dimensions).\n");

// Query. 0.55 is this corpus's starting point with nomic-embed-text, not a rule:
// Ch10.RetrievalEval sweeps it on an annotated set.
FaqSearchService search = new(embeddings, collection, topK: 3, scoreThreshold: 0.55,
    observer: (input, hits) => Console.WriteLine(
        $"  search input: \"{input.ReplaceLineEndings(" | ")}\"\n  retrieved: " +
        (hits.Count == 0 ? "(none above threshold)" : string.Join(", ", hits.Select(h => $"{h.ChunkId} {h.Score:F2}")))));
ChatClientAgent agent = FaqAgentFactory.Create(chatClient, search, mode);
AgentSession session = await agent.CreateSessionAsync(cts.Token);

string[] questions =
[
    "How many PTO days do new employees get?",
    "What is the home office equipment stipend?",
    "What about for contractors?",                          // follow-up: needs the earlier turn
    "How much parental leave do birth parents get?",
    "What is the policy on cryptocurrency investments?",    // not in the handbook
];

foreach (string question in questions)
{
    Console.WriteLine($"You: {question}");
    try
    {
        AgentResponse response = await agent.RunAsync(question, session, cancellationToken: cts.Token);
        Console.WriteLine($"Agent: {response.Text}");
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("[Cancelled]");
        return 130;
    }
    Console.WriteLine(new string('-', 40));
}
return 0;

static IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(IConfiguration config)
{
    string provider = (config["LlmProvider:Provider"] ?? "ollama").ToLowerInvariant();
    return provider switch
    {
        "azure" => new AzureOpenAIClient(
                new Uri(config.GetRequiredValue("LlmProvider:AzureOpenAI:Endpoint")),
                new DefaultAzureCredential())
            .GetEmbeddingClient(config["LlmProvider:AzureOpenAI:EmbeddingModel"] ?? "text-embedding-3-small")
            .AsIEmbeddingGenerator(),
        "openai" => new OpenAI.OpenAIClient(config.GetRequiredValue("LlmProvider:OpenAI:ApiKey"))
            .GetEmbeddingClient(config["LlmProvider:OpenAI:EmbeddingModel"] ?? "text-embedding-3-small")
            .AsIEmbeddingGenerator(),
        // OllamaApiClient implements IEmbeddingGenerator directly; nothing downstream changes.
        "ollama" => new OllamaSharp.OllamaApiClient(
            new Uri(config["LlmProvider:Ollama:Endpoint"] ?? "http://localhost:11434"),
            config["LlmProvider:Ollama:EmbeddingModel"] ?? "nomic-embed-text"),
        _ => throw new InvalidOperationException(
            $"Unsupported provider '{provider}' for embeddings. Use azure, openai, or ollama."),
    };
}
