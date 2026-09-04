// ============================================================================
// Exercise 02 — Document Q&A (Intermediate)
// RAG pipeline: embed documents, store in vector DB, retrieve context, and
// answer questions. Includes 5 sample docs about a fictional company.
// FULLY STANDALONE.
// ============================================================================

using System.ClientModel;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.VectorData;
using CommunityToolkit.VectorData.InMemory;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

using IHost host = builder.Build();
IConfiguration config = host.Services.GetRequiredService<IConfiguration>();

Console.WriteLine("=== Exercise: Document Q&A (RAG Pipeline) ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// ---------------------------------------------------------------
// Step 1: Sample documents about fictional NovaTech Inc.
// ---------------------------------------------------------------
Dictionary<string, string> documents = new()
{
    ["about"] = "NovaTech Inc. was founded in 2020 in Seattle by CEO Sarah Chen. The company specializes in AI-powered developer tools and has grown to 250 employees across three offices in Seattle, London, and Tokyo.",
    ["products"] = "NovaTech's flagship product is CodePilot, an AI pair-programming assistant that supports C#, Python, and TypeScript. They also offer DataLens for data analytics and CloudForge for infrastructure automation. CodePilot has over 50,000 active users.",
    ["culture"] = "NovaTech promotes a remote-first work culture with quarterly in-person gatherings. The company offers unlimited PTO, learning stipends of $3,000 per year, and equity grants for all employees. They emphasize open-source contributions.",
    ["engineering"] = "The engineering team at NovaTech uses .NET 10 and Azure for backend services, React for frontends, and Kubernetes for orchestration. They practice trunk-based development with automated CI/CD pipelines deploying 20 times per day.",
    ["financials"] = "NovaTech reported $45M in annual recurring revenue for 2025, up 120% from the previous year. They closed a Series B round of $80M led by TechVentures Capital. The company expects to reach profitability by Q3 2026."
};

// ---------------------------------------------------------------
// Step 2: Create embeddings and populate vector store
// ---------------------------------------------------------------
Console.WriteLine("Generating embeddings for documents...");

IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator = CreateEmbeddingGenerator(config);
IChatClient chatClient = CreateChatClient(config);

InMemoryCollection<string, DocumentChunk> collection = new("documents");
await collection.EnsureCollectionExistsAsync(cts.Token);

// One batched call, not one call per document — the chapter's own rule.
// Batching amortizes the request overhead and is dramatically faster than
// N round-trips as the corpus grows.
GeneratedEmbeddings<Embedding<float>> embeddings = await embeddingGenerator.GenerateAsync(
    documents.Select(d => d.Value).ToList(), cancellationToken: cts.Token);

foreach (((string id, string content), Embedding<float> embedding) in documents.Zip(embeddings))
{
    await collection.UpsertAsync(new DocumentChunk
    {
        Id = id,
        Content = content,
        Vector = embedding.Vector
    }, cancellationToken: cts.Token);

    Console.WriteLine($"  Embedded: {id} ({content.Length} chars)");
}

Console.WriteLine($"\nIndexed {documents.Count} documents.\n");

// ---------------------------------------------------------------
// Step 3: Q&A loop with RAG
// ---------------------------------------------------------------
ChatClientAgent agent = new(
    chatClient,
    instructions: """
        You are a helpful assistant for NovaTech Inc. Answer questions using ONLY the provided context.
        If the context does not contain enough information, say so clearly.
        Always cite which document the information came from.
        """,
    name: "NovaTechQA");

string[] sampleQuestions =
[
    "Who founded NovaTech and when?",
    "What programming languages does CodePilot support?",
    "How much revenue did NovaTech make in 2025?",
    "What technology stack does the engineering team use?",
    "What benefits does NovaTech offer employees?"
];

foreach (string question in sampleQuestions)
{
    Console.WriteLine($"Q: {question}");

    // Retrieve relevant context
    Embedding<float> queryEmbedding =
        await embeddingGenerator.GenerateAsync(question, cancellationToken: cts.Token);

    List<string> contextChunks = [];
    await foreach (VectorSearchResult<DocumentChunk> result in
        collection.SearchAsync(queryEmbedding.Vector, top: 2, cancellationToken: cts.Token))
    {
        if (result.Record is not null)
        {
            contextChunks.Add($"[{result.Record.Id}]: {result.Record.Content}");
        }
    }

    string context = string.Join("\n\n", contextChunks);

    try
    {
        AgentSession session = await agent.CreateSessionAsync(cts.Token);
        AgentResponse response = await agent.RunAsync(
            $"Context:\n{context}\n\nQuestion: {question}",
            session,
            cancellationToken: cts.Token);

        Console.WriteLine($"A: {response.Text}\n");
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("\n[Cancelled]");
        return;
    }
}

Console.WriteLine("=== Exercise complete! ===");

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
        // The offline default: OllamaApiClient implements IEmbeddingGenerator
        // directly, and nomic-embed-text is the local embedding model the
        // chapter's RAG pipeline uses.
        "ollama" => new OllamaSharp.OllamaApiClient(
            new Uri(configuration["LlmProvider:Ollama:Endpoint"] ?? "http://localhost:11434"),
            configuration["LlmProvider:Ollama:EmbeddingModel"] ?? "nomic-embed-text"),
        _ => throw new InvalidOperationException($"Embedding not supported for provider: '{provider}'")
    };
}

// ---------------------------------------------------------------
// Vector data model
// ---------------------------------------------------------------

/// <summary>
/// A document chunk with its embedding vector for vector search.
/// </summary>
sealed class DocumentChunk
{
    /// <summary>Unique document identifier.</summary>
    [VectorStoreKey]
    public string Id { get; set; } = string.Empty;

    /// <summary>The text content of the chunk.</summary>
    [VectorStoreData]
    public string Content { get; set; } = string.Empty;

    /// <summary>The embedding vector.</summary>
    [VectorStoreVector(1536)]
    public ReadOnlyMemory<float> Vector { get; set; }
}
