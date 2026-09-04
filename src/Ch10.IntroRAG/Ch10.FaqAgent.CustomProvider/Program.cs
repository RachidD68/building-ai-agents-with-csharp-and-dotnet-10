// ============================================================================
// Chapter 10 — Introduction to RAG (custom AIContextProvider — under the hood)
// ----------------------------------------------------------------------------
// The capstone (Ch10.FaqAgent) wires RAG with the built-in TextSearchProvider.
// THIS project is the "Going Deeper" companion: the same FAQ agent, but with a
// hand-rolled RagContextProvider so you can see exactly what the built-in does
// underneath — embed the query, search the store, inject the hits as context.
//
// Reach for a custom provider when you need control the built-in doesn't offer:
// custom reranking, blending multiple retrieval sources, structured context.
//
// The one wart this version carries: the base AIContextProvider.InvokingContext
// doesn't expose the user's message, so the caller sets CurrentQuery before each
// RunAsync. TextSearchProvider avoids that — which is why it's the default.
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
using Ch10.FaqAgent.CustomProvider;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);

using IHost host = builder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();
IConfiguration config = host.Services.GetRequiredService<IConfiguration>();

Console.WriteLine("=== Chapter 10: FAQ Agent — custom AIContextProvider (under the hood) ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// Step 1: Embedding generator (same provider set as the capstone).
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

    "ollama" => new OllamaSharp.OllamaApiClient(
        new Uri(config["LlmProvider:Ollama:Endpoint"] ?? "http://localhost:11434"),
        config["LlmProvider:Ollama:EmbeddingModel"] ?? "nomic-embed-text"),

    _ => throw new InvalidOperationException(
        $"Unsupported provider '{provider}' for embeddings. Use azure, openai, or ollama.")
};

// Step 2: Ingest the FAQ corpus into the vector store.
InMemoryCollection<string, FaqChunk> collection = new("faq");
await collection.EnsureCollectionExistsAsync(cts.Token);

string docsPath = Path.Combine(AppContext.BaseDirectory, "docs");
if (!Directory.Exists(docsPath))
{
    Console.WriteLine("Docs directory not found. Ensure FAQ .md files are in the docs/ folder.");
    return;
}

string[] mdFiles = Directory.GetFiles(docsPath, "*.md");
List<FaqChunk> allChunks = [];

foreach (string filePath in mdFiles)
{
    string fileName = Path.GetFileNameWithoutExtension(filePath);
    string content = await File.ReadAllTextAsync(filePath, cts.Token);
    string topic = content.Split('\n', 2)[0].TrimStart('#', ' ');

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

// Step 3: Wire the CUSTOM provider.
RagContextProvider ragProvider = new(collection, embeddingGenerator, topK: 3);

string instructions = """
    You are the Contoso FAQ Agent. Answer employee questions about company
    policies using ONLY the retrieved documents in your context. If the answer
    is not there, say "I don't have information about that in our knowledge
    base. Please contact HR at hr@contoso.com." Cite the source. Be concise.
    """;

ChatClientAgent agent = new(chatClient, new ChatClientAgentOptions
{
    Name = "ContosoFaqAgent",
    Description = "Contoso FAQ agent (custom RAG context provider)",
    AIContextProviders = [ragProvider],
    ChatOptions = new ChatOptions
    {
        Instructions = instructions,
        Temperature = 0.2f,
        MaxOutputTokens = 600,
    }
});

string[] questions =
[
    "How many PTO days do new employees get?",
    "What is the policy on cryptocurrency investments?", // Out of scope
];

// One session for the whole conversation — a fresh session per question would
// throw away the very conversation memory the provider pattern exists to feed.
AgentSession session = await agent.CreateSessionAsync(cts.Token);

foreach (string question in questions)
{
    Console.WriteLine($"You: {question}");

    // The wart: tell the provider what to retrieve for, since InvokingContext
    // doesn't expose the user's message to a plain AIContextProvider.
    ragProvider.CurrentQuery = question;

    AgentResponse response = await agent.RunAsync(question, session, cancellationToken: cts.Token);

    Console.WriteLine($"Agent: {response.Text}");
    Console.WriteLine(new string('-', 40));
}

Console.WriteLine("\n=== Custom-provider demo complete! ===");
