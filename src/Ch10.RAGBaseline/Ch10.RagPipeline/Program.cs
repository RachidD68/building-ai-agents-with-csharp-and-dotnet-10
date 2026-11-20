// ============================================================================
// The RAG pipeline: check the model -> ingest -> chunk -> embed -> store -> retrieve
// ----------------------------------------------------------------------------
// Pipeline.cs holds the steps; this file runs them against a real embedding
// model (Ollama nomic-embed-text by default, 768 dimensions).
//
//   dotnet run                  the pipeline over data/*.txt, four queries
//   dotnet run -- --self-test   fixed vectors, no model; RESULT n/n
// ============================================================================

using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.VectorData;
using CommunityToolkit.VectorData.InMemory;
using Ch10.RagPipeline;

if (args.Contains("--self-test")) return await SelfTest.RunAsync();

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

using IHost host = builder.Build();
IConfiguration config = host.Services.GetRequiredService<IConfiguration>();

Console.WriteLine("=== RAG pipeline: ingest, chunk, embed, store, retrieve ===");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

string provider = (config["LlmProvider:Provider"] ?? "ollama").ToLowerInvariant();
IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator = provider switch
{
    "azure" => new AzureOpenAIClient(
            new Uri(config.GetRequiredValue("LlmProvider:AzureOpenAI:Endpoint")),
            new DefaultAzureCredential())
        .GetEmbeddingClient(config["LlmProvider:AzureOpenAI:EmbeddingModel"] ?? "text-embedding-3-small")
        .AsIEmbeddingGenerator(),

    "openai" => new OpenAI.OpenAIClient(config.GetRequiredValue("LlmProvider:OpenAI:ApiKey"))
        .GetEmbeddingClient(config["LlmProvider:OpenAI:EmbeddingModel"] ?? "text-embedding-3-small")
        .AsIEmbeddingGenerator(),

    // OllamaApiClient implements IEmbeddingGenerator<string, Embedding<float>>
    // directly, so the rest of the pipeline does not change.
    "ollama" => new OllamaSharp.OllamaApiClient(
        new Uri(config["LlmProvider:Ollama:Endpoint"] ?? "http://localhost:11434"),
        config["LlmProvider:Ollama:EmbeddingModel"] ?? "nomic-embed-text"),

    _ => throw new InvalidOperationException(
        $"Unsupported provider '{provider}' for embeddings. Use azure, openai, or ollama.")
};

// Step 0: the vector size is part of the schema. Check it before reading a file.
await Pipeline.CheckModelAsync(embeddingGenerator, cts.Token);
Console.WriteLine($"Embedding model checked: {DocumentChunk.Dimensions} dimensions.");

// Steps 1-3: ingest and chunk the text files, embed in one batch, store.
string dataPath = Path.Combine(AppContext.BaseDirectory, "data");
if (!Directory.Exists(dataPath))
{
    Console.WriteLine("Creating the sample data files...");
    Directory.CreateDirectory(dataPath);
    CreateSampleDataFiles(dataPath);
}

List<DocumentChunk> allChunks = [];
foreach (string filePath in Directory.GetFiles(dataPath, "*.txt").Order(StringComparer.Ordinal))
{
    string content = await File.ReadAllTextAsync(filePath, cts.Token);
    DocumentChunk[] fileChunks = [.. Pipeline.Chunk(Path.GetFileName(filePath), content)];
    Console.WriteLine($"  {Path.GetFileName(filePath)}: {fileChunks.Length} chunks");
    allChunks.AddRange(fileChunks);
}

InMemoryCollection<string, DocumentChunk> collection = new("documents");
await collection.EnsureCollectionExistsAsync(cts.Token);
await Pipeline.IngestAsync(embeddingGenerator, collection, allChunks, cts.Token);
Console.WriteLine($"Stored {allChunks.Count} chunks.");
Console.WriteLine();

// Step 4: retrieve.
string[] queries =
[
    "What are the company values?",
    "How does the performance review process work?",
    "What benefits does the company offer?",
    "Tell me about the engineering team structure."
];

foreach (string query in queries)
{
    Console.WriteLine($"Query: \"{query}\"");
    int rank = 1;
    foreach (VectorSearchResult<DocumentChunk> result in
             await Pipeline.SearchAsync(embeddingGenerator, collection, query, top: 3, cts.Token))
    {
        string preview = result.Record.Content.Length > 80
            ? string.Concat(result.Record.Content.AsSpan(0, 80), "...")
            : result.Record.Content;
        Console.WriteLine($"  #{rank++} {result.Score:F4} [{result.Record.Id}] {preview}");
    }
    Console.WriteLine();
}

return 0;

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
