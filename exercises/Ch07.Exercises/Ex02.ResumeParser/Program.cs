// ============================================================================
// Exercise 02 — Resume Parser (Intermediate)
// Parse a resume/CV text into structured data using RunAsync<T>().
// Handles missing fields gracefully. FULLY STANDALONE.
// ============================================================================

using System.ClientModel;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

using IHost host = builder.Build();
IConfiguration config = host.Services.GetRequiredService<IConfiguration>();
IChatClient chatClient = CreateChatClient(config);

Console.WriteLine("=== Exercise: Resume Parser ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

ChatClientAgent agent = new(
    chatClient,
    instructions: """
        You are a resume parser. Extract structured data from resume text.
        Return ONLY valid JSON matching the requested schema.
        If a field cannot be determined from the text, use null.
        For lists, return an empty array if no items are found.
        """,
    name: "ResumeParser");

string sampleResume = """
    Jane Smith
    jane.smith@techcorp.dev

    SKILLS
    C#, .NET, Azure, Docker, Kubernetes, SQL Server, TypeScript, React

    EXPERIENCE

    Senior Software Engineer at TechCorp Inc. (2021 - Present)
    Lead developer for cloud migration initiatives. Architected microservices
    platform serving 2M daily users.

    Software Engineer at StartupXYZ (2018 - 2021)
    Full-stack developer building SaaS products. Introduced CI/CD pipelines
    that reduced deployment time by 70%.

    Junior Developer at WebAgency (2016 - 2018)
    Built responsive web applications for enterprise clients.

    EDUCATION

    M.Sc. Computer Science, MIT (2016)
    B.Sc. Software Engineering, UC Berkeley (2014)
    """;

Console.WriteLine("--- Input Resume ---");
Console.WriteLine(sampleResume.Trim());
Console.WriteLine();

try
{
    AgentSession session = await agent.CreateSessionAsync(cts.Token);
    AgentResponse<ParsedResume> response = await agent.RunAsync<ParsedResume>(
        $"Parse this resume:\n\n{sampleResume}",
        session,
        cancellationToken: cts.Token);

    if (response.Result is { } resume)
    {
        Console.WriteLine("--- Parsed Resume ---\n");
        Console.WriteLine($"  Name  : {resume.Name ?? "N/A"}");
        Console.WriteLine($"  Email : {resume.Email ?? "N/A"}");

        Console.WriteLine($"\n  Skills ({resume.Skills?.Count ?? 0}):");
        foreach (string skill in resume.Skills ?? [])
        {
            Console.WriteLine($"    - {skill}");
        }

        Console.WriteLine($"\n  Experience ({resume.Experience?.Count ?? 0}):");
        foreach (ExperienceEntry exp in resume.Experience ?? [])
        {
            Console.WriteLine($"    - {exp.Role ?? "Unknown"} at {exp.Company ?? "Unknown"} ({exp.Duration ?? "N/A"})");
        }

        Console.WriteLine($"\n  Education ({resume.Education?.Count ?? 0}):");
        foreach (string edu in resume.Education ?? [])
        {
            Console.WriteLine($"    - {edu}");
        }
    }
    else
    {
        Console.WriteLine("  (No structured result returned)");
        Console.WriteLine($"  Raw: {response.Text}");
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n[Cancelled]");
}

Console.WriteLine("\n=== Exercise complete! ===");

// ---------------------------------------------------------------
// Provider factory
// ---------------------------------------------------------------

/// <summary>
/// Creates an <see cref="IChatClient"/> from User Secrets configuration.
/// </summary>
static IChatClient CreateChatClient(IConfiguration configuration)
{
    string provider = configuration["LlmProvider:Provider"]?.ToLowerInvariant() ?? "github";

    return provider switch
    {
        "azure" => new AzureOpenAIClient(
                new Uri(configuration["LlmProvider:AzureOpenAI:Endpoint"] ?? throw new InvalidOperationException("Missing endpoint")),
                new DefaultAzureCredential())
            .GetChatClient(configuration["LlmProvider:AzureOpenAI:Model"] ?? "gpt-4o-mini")
            .AsIChatClient(),

        "openai" => new OpenAI.OpenAIClient(
                configuration["LlmProvider:OpenAI:ApiKey"] ?? throw new InvalidOperationException("Missing key"))
            .GetChatClient(configuration["LlmProvider:OpenAI:Model"] ?? "gpt-4o-mini")
            .AsIChatClient(),

        "github" => new OpenAI.OpenAIClient(
                new ApiKeyCredential(configuration["LlmProvider:GitHub:Token"] ?? throw new InvalidOperationException("Missing token")),
                new OpenAI.OpenAIClientOptions { Endpoint = new Uri(configuration["LlmProvider:GitHub:Endpoint"] ?? "https://models.github.ai/inference") })
            .GetChatClient(configuration["LlmProvider:GitHub:Model"] ?? "openai/gpt-4o-mini")
            .AsIChatClient(),

        "ollama" => new OllamaSharp.OllamaApiClient(
            new Uri(configuration["LlmProvider:Ollama:Endpoint"] ?? "http://localhost:11434"),
            configuration["LlmProvider:Ollama:Model"] ?? "llama3.2"),

        _ => throw new InvalidOperationException($"Unknown provider: '{provider}'")
    };
}

// ---------------------------------------------------------------
// Data models
// ---------------------------------------------------------------

/// <summary>
/// Parsed resume data.
/// </summary>
sealed class ParsedResume
{
    /// <summary>Candidate name.</summary>
    public string? Name { get; set; }

    /// <summary>Candidate email.</summary>
    public string? Email { get; set; }

    /// <summary>List of technical skills.</summary>
    public List<string>? Skills { get; set; }

    /// <summary>Work experience entries.</summary>
    public List<ExperienceEntry>? Experience { get; set; }

    /// <summary>Education entries.</summary>
    public List<string>? Education { get; set; }
}

/// <summary>
/// A single work experience entry.
/// </summary>
sealed class ExperienceEntry
{
    /// <summary>Company name.</summary>
    public string? Company { get; set; }

    /// <summary>Job role/title.</summary>
    public string? Role { get; set; }

    /// <summary>Duration (e.g., "2021 - Present").</summary>
    public string? Duration { get; set; }
}
