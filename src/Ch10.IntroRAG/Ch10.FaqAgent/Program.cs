// ============================================================================
// Chapter 10 — Introduction to RAG (FAQ Agent — Part III Capstone)
// ----------------------------------------------------------------------------
// The Contoso FAQ agent, grounded in a Markdown knowledge base. This is the
// RECOMMENDED wiring: MAF's built-in TextSearchProvider runs a search before
// every agent call and injects the hits as context — citations included.
//
// The retrieval logic lives in FaqSearchService (embed -> vector search ->
// keep strong matches), the same pipeline taught in Ch10.RagPipeline. We just
// hand it to the framework's provider instead of hand-rolling the injection.
//
// Key concepts:
//   - TextSearchProvider with SearchTime = BeforeAIInvoke
//   - RecentMessageMemoryLimit so follow-up questions retrieve correctly
//   - Citations for free via TextSearchResult.SourceName
//   - Grounding instruction => "I don't know" fires on no good hit
//   - The under-the-hood custom AIContextProvider lives in
//     Ch10.FaqAgent.CustomProvider (see that project / the "Going Deeper" box).
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

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);

using IHost host = builder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();
IConfiguration config = host.Services.GetRequiredService<IConfiguration>();

Console.WriteLine("=== Chapter 10: FAQ Agent — Part III Capstone ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// ---------------------------------------------------------------
// Step 1: Create the embedding generator
// ---------------------------------------------------------------
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

    // Local, offline embeddings. OllamaApiClient implements IEmbeddingGenerator
    // directly, so nothing downstream changes — only the vector dimension differs.
    "ollama" => new OllamaSharp.OllamaApiClient(
        new Uri(config["LlmProvider:Ollama:Endpoint"] ?? "http://localhost:11434"),
        config["LlmProvider:Ollama:EmbeddingModel"] ?? "nomic-embed-text"),

    _ => throw new InvalidOperationException(
        $"Unsupported provider '{provider}' for embeddings. Use azure, openai, or ollama.")
};

// ---------------------------------------------------------------
// Step 2: Ingest FAQ documents into the vector store
// ---------------------------------------------------------------
InMemoryCollection<string, FaqChunk> collection = new("faq");
await collection.EnsureCollectionExistsAsync(cts.Token);

string docsPath = Path.Combine(AppContext.BaseDirectory, "docs");
if (!Directory.Exists(docsPath))
{
    Console.WriteLine("Docs directory not found. Ensure FAQ .md files are in the docs/ folder.");
    return;
}

string[] mdFiles = Directory.GetFiles(docsPath, "*.md");
Console.WriteLine($"Ingesting {mdFiles.Length} FAQ documents...\n");

List<FaqChunk> allChunks = [];

foreach (string filePath in mdFiles)
{
    string fileName = Path.GetFileNameWithoutExtension(filePath);
    string content = await File.ReadAllTextAsync(filePath, cts.Token);

    // Extract title from first line
    string topic = content.Split('\n', 2)[0].TrimStart('#', ' ');

    // Chunk by paragraph
    string[] paragraphs = content.Split(
        ["\r\n\r\n", "\n\n"],
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    for (int i = 0; i < paragraphs.Length; i++)
    {
        if (paragraphs[i].Length < 30) continue;

        allChunks.Add(new FaqChunk
        {
            Id = $"{fileName}-{i}",
            Content = paragraphs[i],
            Source = fileName,
            Topic = topic
        });
    }
}

// Generate embeddings in batch
string[] texts = allChunks.Select(c => c.Content).ToArray();
GeneratedEmbeddings<Embedding<float>> embeddings = await embeddingGenerator.GenerateAsync(
    texts,
    cancellationToken: cts.Token);

for (int i = 0; i < allChunks.Count; i++)
{
    allChunks[i].Embedding = embeddings[i].Vector;
    await collection.UpsertAsync(allChunks[i], cts.Token);
}

Console.WriteLine($"Indexed {allChunks.Count} chunks from {mdFiles.Length} documents.\n");

// ---------------------------------------------------------------
// Step 3: Wire RAG with the built-in TextSearchProvider
// ---------------------------------------------------------------
// FaqSearchService is the retrieval pipeline (embed -> search -> keep strong
// matches). We give its SearchAsync method to TextSearchProvider, which runs it
// before every agent call and injects the formatted hits (with citations).
FaqSearchService faqSearch = new(embeddingGenerator, collection, topK: 3, scoreThreshold: 0.55f);

TextSearchProvider ragProvider = new(
    faqSearch.SearchAsync,
    new TextSearchProviderOptions
    {
        // Search automatically before each invocation and inject the results.
        SearchTime = TextSearchProviderOptions.TextSearchBehavior.BeforeAIInvoke,

        // Prepend the last few user/assistant turns to the search input so a
        // follow-up like "what about for contractors?" still retrieves correctly.
        RecentMessageMemoryLimit = 6,
    });

// The grounding rule lives in the agent's own instructions. When retrieval
// returns nothing (weak matches filtered out), no context is injected and this
// instruction produces the "I don't know" answer.
string instructions = """
    You are the Contoso FAQ Agent. Answer employee questions about company
    policies, benefits, procedures, and culture.

    Rules:
    - Answer ONLY from the provided context.
    - If the context does not contain the answer, reply:
      "I don't have information about that in our knowledge base. Please contact
      HR at hr@contoso.com or the relevant department for assistance."
    - Cite the source document when you can.
    - Be concise. Never invent policies or numbers.
    """;

ChatClientAgentOptions options = new()
{
    Name = "ContosoFaqAgent",
    Description = "Contoso company FAQ agent grounded in internal documents",
    AIContextProviders = [ragProvider],
    ChatOptions = new ChatOptions
    {
        Instructions = instructions,
        Temperature = 0.2f,
        MaxOutputTokens = 600,
    }
};

ChatClientAgent agent = new(chatClient, options);

// ---------------------------------------------------------------
// Step 4: Q&A over a single session (so recent-message memory works)
// ---------------------------------------------------------------
Console.WriteLine(new string('=', 60));
Console.WriteLine("CONTOSO FAQ AGENT");
Console.WriteLine(new string('=', 60));

AgentSession session = await agent.CreateSessionAsync(cts.Token);

string[] sampleQuestions =
[
    "How many PTO days do new employees get?",
    "What is the home office equipment stipend?",
    // Follow-up: only resolvable because recent turns feed the search input.
    "What about for contractors?",
    "How much parental leave do birth parents get?",
    "What is the policy on cryptocurrency investments?", // Out of scope -> "I don't know"
];

foreach (string question in sampleQuestions)
{
    Console.WriteLine($"\nYou: {question}");

    try
    {
        AgentResponse response = await agent.RunAsync(
            question,
            session,
            cancellationToken: cts.Token);

        Console.WriteLine($"Agent: {response.Text}");
        Console.WriteLine(new string('-', 40));
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("\n[Cancelled by user]");
        return;
    }
}

Console.WriteLine("\n=== FAQ Agent demo complete! ===");
