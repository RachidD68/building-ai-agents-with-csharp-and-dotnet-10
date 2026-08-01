// ============================================================================
// Chapter 10 — Introduction to RAG (Full RAG Pipeline)
// Demonstrates the complete Retrieval-Augmented Generation pipeline:
//   Ingest -> Chunk -> Embed -> Store -> Retrieve
//
// Key concepts:
//   - Microsoft.Extensions.VectorData.Abstractions for the vector store interface
//   - InMemoryCollection from Semantic Kernel for in-memory vector storage
//   - IEmbeddingGenerator for generating embeddings
//   - Cosine similarity search for retrieval
//   - Paragraph-based chunking strategy
// ============================================================================

using System.Numerics.Tensors;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.VectorData;
using CommunityToolkit.VectorData.InMemory;
using Ch10.RagPipeline;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

using IHost host = builder.Build();
IConfiguration config = host.Services.GetRequiredService<IConfiguration>();

Console.WriteLine("=== Chapter 10: RAG Pipeline — Ingest, Chunk, Embed, Store, Retrieve ===\n");

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

    "github" => new OpenAI.OpenAIClient(
            new System.ClientModel.ApiKeyCredential(config.GetRequiredValue("LlmProvider:GitHub:Token")),
            new OpenAI.OpenAIClientOptions
            {
                Endpoint = new Uri(config["LlmProvider:GitHub:Endpoint"] ?? "https://models.github.ai/inference")
            })
        .GetEmbeddingClient(config["LlmProvider:GitHub:EmbeddingModel"] ?? "text-embedding-3-small")
        .AsIEmbeddingGenerator(),

    // Local, offline embeddings via Ollama. OllamaApiClient implements
    // IEmbeddingGenerator<string, Embedding<float>> directly, so the rest of the
    // pipeline is identical — only the dimension differs (nomic-embed-text is 768).
    "ollama" => new OllamaSharp.OllamaApiClient(
        new Uri(config["LlmProvider:Ollama:Endpoint"] ?? "http://localhost:11434"),
        config["LlmProvider:Ollama:EmbeddingModel"] ?? "nomic-embed-text"),

    _ => throw new InvalidOperationException(
        $"Unsupported provider '{provider}' for embeddings. Use azure, openai, github, or ollama.")
};

// ---------------------------------------------------------------
// Step 2: Create the in-memory vector store collection
// ---------------------------------------------------------------
InMemoryCollection<string, DocumentChunk> collection = new("documents");
await collection.EnsureCollectionExistsAsync(cts.Token);

Console.WriteLine("Vector store collection created: 'documents'\n");

// ---------------------------------------------------------------
// Step 3: INGEST — Load text files from the data/ directory
// ---------------------------------------------------------------
string dataPath = Path.Combine(AppContext.BaseDirectory, "data");
if (!Directory.Exists(dataPath))
{
    Console.WriteLine($"Data directory not found: {dataPath}");
    Console.WriteLine("Creating sample data files...");
    Directory.CreateDirectory(dataPath);
    CreateSampleDataFiles(dataPath);
}

string[] textFiles = Directory.GetFiles(dataPath, "*.txt");
Console.WriteLine($"Found {textFiles.Length} text files to ingest.\n");

// ---------------------------------------------------------------
// Step 4: CHUNK — Split documents by paragraph
// ---------------------------------------------------------------
List<DocumentChunk> allChunks = [];

foreach (string filePath in textFiles)
{
    string fileName = Path.GetFileName(filePath);
    string content = await File.ReadAllTextAsync(filePath, cts.Token);

    // Split by double newline (paragraph boundary)
    string[] paragraphs = content.Split(
        ["\r\n\r\n", "\n\n"],
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    Console.WriteLine($"  {fileName}: {paragraphs.Length} chunks");

    for (int i = 0; i < paragraphs.Length; i++)
    {
        if (paragraphs[i].Length < 20) continue; // Skip very short paragraphs

        allChunks.Add(new DocumentChunk
        {
            Id = $"{fileName}-{i}",
            Content = paragraphs[i],
            SourceFile = fileName,
            ChunkIndex = i
        });
    }
}

Console.WriteLine($"\nTotal chunks: {allChunks.Count}\n");

// ---------------------------------------------------------------
// Step 5: EMBED — Generate embeddings for all chunks
// ---------------------------------------------------------------
Console.WriteLine("Generating embeddings for all chunks...");

// Batch embedding generation for efficiency
string[] texts = allChunks.Select(c => c.Content).ToArray();
GeneratedEmbeddings<Embedding<float>> embeddings = await embeddingGenerator.GenerateAsync(
    texts,
    cancellationToken: cts.Token);

for (int i = 0; i < allChunks.Count; i++)
{
    allChunks[i].Embedding = embeddings[i].Vector;
}

Console.WriteLine($"Generated {embeddings.Count} embeddings.\n");

// ---------------------------------------------------------------
// Step 6: STORE — Upsert chunks into the vector store
// ---------------------------------------------------------------
Console.WriteLine("Storing chunks in vector store...");

foreach (DocumentChunk chunk in allChunks)
{
    await collection.UpsertAsync(chunk, cts.Token);
}

Console.WriteLine($"Stored {allChunks.Count} chunks.\n");

// ---------------------------------------------------------------
// Step 7: RETRIEVE — Search by query
// ---------------------------------------------------------------
string[] queries =
[
    "What are the company values?",
    "How does the performance review process work?",
    "What benefits does the company offer?",
    "Tell me about the engineering team structure."
];

Console.WriteLine(new string('=', 60));
Console.WriteLine("RETRIEVAL RESULTS");
Console.WriteLine(new string('=', 60));

foreach (string query in queries)
{
    Console.WriteLine($"\nQuery: \"{query}\"");
    Console.WriteLine(new string('-', 40));

    // Generate embedding for the query
    Embedding<float> queryEmbedding = await embeddingGenerator.GenerateAsync(
        query,
        cancellationToken: cts.Token);

    // Search the vector store
    var results = collection.SearchAsync(
        queryEmbedding.Vector,
        top: 3,
        cancellationToken: cts.Token);

    int rank = 1;
    await foreach (VectorSearchResult<DocumentChunk> result in results)
    {
        string preview = result.Record.Content.Length > 120
            ? string.Concat(result.Record.Content.AsSpan(0, 120), "...")
            : result.Record.Content;

        Console.WriteLine($"  #{rank} (score: {result.Score:F4}) [{result.Record.SourceFile}]");
        Console.WriteLine($"     {preview}\n");
        rank++;
    }
}

Console.WriteLine("=== RAG Pipeline demo complete! ===");

// ---------------------------------------------------------------
// Helper: Create sample data files
// ---------------------------------------------------------------
static void CreateSampleDataFiles(string path)
{
    File.WriteAllText(Path.Combine(path, "company-values.txt"),
        """
        Contoso Company Values

        At Contoso, our values define who we are and how we work together. These principles guide every decision we make.

        Integrity First: We believe in doing the right thing, even when no one is watching. Honesty and transparency are non-negotiable in all our business dealings and internal communications.

        Customer Obsession: Our customers are at the heart of everything we do. We listen actively, respond quickly, and continuously strive to exceed their expectations.

        Innovation Without Fear: We encourage bold thinking and calculated risk-taking. Failure is a learning opportunity, not a reason for blame. We invest heavily in research and development.

        One Team: Collaboration across teams and departments is how we achieve extraordinary results. We celebrate diverse perspectives and believe that inclusion drives better outcomes.

        Sustainability Matters: We are committed to reducing our environmental footprint. From our data centers to our offices, we pursue sustainable practices in every aspect of our operations.
        """);

    File.WriteAllText(Path.Combine(path, "performance-reviews.txt"),
        """
        Contoso Performance Review Process

        Our performance review process is designed to support growth, recognize achievements, and align individual goals with company objectives.

        Review Cycle: Performance reviews are conducted twice a year — a mid-year check-in in June and a full annual review in December. Both reviews involve self-assessment, peer feedback, and manager evaluation.

        Goal Setting: At the start of each year, employees work with their managers to set 3-5 SMART goals. Goals should align with team objectives and the company's annual priorities.

        Self-Assessment: Before each review, employees complete a self-assessment reflecting on their progress toward goals, key accomplishments, challenges faced, and areas for development.

        Peer Feedback: Employees nominate 3-5 peers to provide feedback. Peer reviews focus on collaboration, communication, and technical contributions. All feedback is shared with the employee.

        Rating Scale: Performance is rated on a 5-point scale. Exceeds Expectations, Meets Expectations Plus, Meets Expectations, Needs Improvement, and Unsatisfactory. Ratings inform compensation decisions.

        Development Plans: Every employee creates a development plan identifying skills to build, training to pursue, and career aspirations. Managers are responsible for supporting these plans.
        """);

    File.WriteAllText(Path.Combine(path, "benefits-overview.txt"),
        """
        Contoso Employee Benefits Overview

        Contoso offers a comprehensive benefits package designed to support the health, financial security, and well-being of our employees and their families.

        Health Insurance: We offer three medical plan options — a PPO, an HMO, and a high-deductible plan with HSA. Dental and vision coverage are included at no additional cost. Contoso covers 90% of premiums for employees and 75% for dependents.

        Retirement: Contoso matches 401(k) contributions up to 6% of salary. Matching contributions vest immediately. Financial planning services are available at no cost through our partnership with Fidelity.

        Paid Time Off: New employees receive 15 days of PTO annually, increasing to 20 days after 3 years and 25 days after 6 years. Contoso also observes 11 company holidays.

        Parental Leave: Birth parents receive 16 weeks of fully paid leave. Non-birth parents receive 8 weeks. Adoption leave is 12 weeks. All leave must begin within 12 months of the qualifying event.

        Learning and Development: Each employee has a $2,000 annual budget for professional development including conferences, courses, certifications, and books. Tuition reimbursement up to $5,250 per year is also available.

        Wellness Program: Contoso provides a $500 annual wellness stipend for gym memberships, fitness equipment, or mental health services. Free counseling sessions are available through our Employee Assistance Program.
        """);

    File.WriteAllText(Path.Combine(path, "engineering-team.txt"),
        """
        Contoso Engineering Team Structure

        The engineering organization at Contoso is structured to promote autonomy, collaboration, and technical excellence across all product lines.

        Organization: Engineering is divided into four pillars — Platform, Product, Data, and Infrastructure. Each pillar is led by a VP of Engineering who reports to the CTO.

        Team Structure: Teams follow a pod model with 5-8 engineers, a tech lead, a product manager, and a designer. Pods own their domain end-to-end, from design through deployment and monitoring.

        Engineering Levels: Contoso has six engineering levels. Junior Engineer (L1), Engineer (L2), Senior Engineer (L3), Staff Engineer (L4), Principal Engineer (L5), and Distinguished Engineer (L6). Promotion criteria are transparent and published on the engineering wiki.

        Tech Stack: Our primary stack is .NET and C# for backend services, React with TypeScript for web frontends, and Swift/Kotlin for mobile apps. We run on Azure with Kubernetes orchestration.

        Development Practices: All code goes through pull request review with at least two approvals. We practice continuous integration and deployment with automated testing gates. Feature flags are used for safe rollouts.

        On-Call: Each pod maintains an on-call rotation with primary and secondary responders. On-call shifts are one week long. Contoso compensates on-call duty with additional PTO and a stipend.
        """);
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
