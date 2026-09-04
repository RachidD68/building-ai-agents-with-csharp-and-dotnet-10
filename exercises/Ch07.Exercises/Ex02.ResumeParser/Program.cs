// ============================================================================
// Exercise 02 — Resume Parser (Intermediate)
// Extracts a Resume record from pasted resume text: candidate name, contact
// info (email AND phone), skills, experiences as WorkExperience records with
// DateTime? start/end dates, and a list of Education entry records. DateTime?
// covers fields that may be missing — a current job has no end date. Under
// strict mode the nullable means "the key is always present and may be null",
// so the instructions tell the model to set endDate to null for ongoing jobs
// rather than omitting it. Every property carries a [Description] schema
// hint, and JsonException failures use the chapter's retry-once pattern.
// FULLY STANDALONE.
// ============================================================================

using System.ClientModel;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
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

JsonSerializerOptions jsonOptions = new()
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
};

ChatClientAgent agent = new(
    chatClient,
    instructions: """
        You are a resume parser. Extract structured data from resume text.
        Return ONLY valid JSON matching the requested schema.
        Dates must be ISO-8601 (yyyy-MM-dd). When only a year is given, use
        January 1 of that year. For ongoing jobs ("Present"), set endDate to
        null — include the key, do not omit it.
        For lists, return an empty array if no items are found.
        """,
    name: "ResumeParser");

string sampleResume = """
    Jane Smith
    jane.smith@techcorp.dev | +1 (206) 555-0142

    SKILLS
    C#, .NET, Azure, Docker, Kubernetes, SQL Server, TypeScript, React

    EXPERIENCE

    Senior Software Engineer at TechCorp Inc. (June 2021 - Present)
    Lead developer for cloud migration initiatives. Architected microservices
    platform serving 2M daily users.

    Software Engineer at StartupXYZ (March 2018 - June 2021)
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
    Resume? resume = await ParseResumeAsync(sampleResume);

    if (resume is not null)
    {
        Console.WriteLine("--- Parsed Resume ---\n");
        Console.WriteLine($"  Name  : {resume.Name}");
        Console.WriteLine($"  Email : {resume.Email}");
        Console.WriteLine($"  Phone : {resume.Phone}");

        Console.WriteLine($"\n  Skills ({resume.Skills.Count}):");
        foreach (string skill in resume.Skills)
        {
            Console.WriteLine($"    - {skill}");
        }

        Console.WriteLine($"\n  Experience ({resume.Experiences.Count}):");
        foreach (WorkExperience exp in resume.Experiences)
        {
            string start = exp.StartDate?.ToString("yyyy-MM") ?? "?";
            string end = exp.EndDate?.ToString("yyyy-MM") ?? "Present";
            Console.WriteLine($"    - {exp.Title} at {exp.Company} ({start} to {end})");
            Console.WriteLine($"      {exp.Description}");
        }

        Console.WriteLine($"\n  Education ({resume.Education.Count}):");
        foreach (Education edu in resume.Education)
        {
            string year = edu.GraduationYear?.ToString() ?? "?";
            Console.WriteLine($"    - {edu.Degree}, {edu.Institution} ({year})");
        }

        Console.WriteLine("\n  Compare against the input: which fields did the model get wrong?");
        Console.WriteLine("  Dates are the usual suspects — year-only ranges and 'Present'.");
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n[Cancelled]");
}

Console.WriteLine("\n=== Exercise complete! ===");

// ---------------------------------------------------------------
// Typed call with the chapter's recovery pattern:
// one attempt, one stricter retry on JsonException, never a loop.
// ---------------------------------------------------------------
async Task<Resume?> ParseResumeAsync(string resumeText)
{
    string prompt = $"Parse this resume:\n\n{resumeText}";

    // Attempt 1: the ordinary typed call.
    try
    {
        AgentSession session = await agent.CreateSessionAsync(cts.Token);
        AgentResponse<Resume> response = await agent.RunAsync<Resume>(
            prompt, session, jsonOptions, cancellationToken: cts.Token);
        return response.Result;
    }
    catch (JsonException ex)
    {
        Console.WriteLine($"  [retry] Typed run failed to deserialize ({ex.Message}); retrying once with a stricter prompt.");
    }

    // Attempt 2: one stricter retry, on a fresh session — a fresh session
    // avoids carrying the malformed exchange forward as context.
    string stricterPrompt =
        prompt +
        "\n\nIMPORTANT: Respond with valid JSON ONLY. No prose, no markdown, no code fences. " +
        "Every field is required. Use the exact field names from the schema.";

    try
    {
        AgentSession retrySession = await agent.CreateSessionAsync(cts.Token);
        AgentResponse<Resume> retry = await agent.RunAsync<Resume>(
            stricterPrompt, retrySession, jsonOptions, cancellationToken: cts.Token);
        return retry.Result;
    }
    catch (JsonException ex)
    {
        Console.WriteLine($"\nJSON deserialization error after one retry: {ex.Message}");
        Console.WriteLine("The LLM response did not match the expected schema.");
        Console.WriteLine("This can happen with less capable models. Try a different model or");
        Console.WriteLine("add more explicit schema instructions to the system prompt.");
        return null;
    }
}

// ---------------------------------------------------------------
// Provider factory
// ---------------------------------------------------------------

/// <summary>
/// Creates an <see cref="IChatClient"/> from User Secrets configuration.
/// </summary>
static IChatClient CreateChatClient(IConfiguration configuration)
{
    string provider = configuration["LlmProvider:Provider"]?.ToLowerInvariant() ?? "ollama";

    return provider switch
    {
        "azure" => new AzureOpenAIClient(
                new Uri(configuration["LlmProvider:AzureOpenAI:Endpoint"] ?? throw new InvalidOperationException("Missing endpoint")),
                new DefaultAzureCredential())
            .GetChatClient(configuration["LlmProvider:AzureOpenAI:Model"] ?? "gpt-5.6-luna")
            .AsIChatClient(),

        "openai" => new OpenAI.OpenAIClient(
                configuration["LlmProvider:OpenAI:ApiKey"] ?? throw new InvalidOperationException("Missing key"))
            .GetChatClient(configuration["LlmProvider:OpenAI:Model"] ?? "gpt-5.6-luna")
            .AsIChatClient(),

        "ollama" => new OllamaSharp.OllamaApiClient(
            new Uri(configuration["LlmProvider:Ollama:Endpoint"] ?? "http://localhost:11434"),
            configuration["LlmProvider:Ollama:Model"] ?? "llama3.2"),

        _ => throw new InvalidOperationException($"Unknown provider: '{provider}'")
    };
}

// ---------------------------------------------------------------
// Data models — records with [Description] on every property
// ---------------------------------------------------------------

/// <summary>
/// A resume extracted from pasted text.
/// </summary>
sealed record Resume
{
    [Description("The candidate's full name.")]
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [Description("The candidate's email address.")]
    [JsonPropertyName("email")]
    public string Email { get; init; } = string.Empty;

    [Description("The candidate's phone number, as written in the resume.")]
    [JsonPropertyName("phone")]
    public string Phone { get; init; } = string.Empty;

    [Description("The candidate's skills, one entry per skill.")]
    [JsonPropertyName("skills")]
    public IReadOnlyList<string> Skills { get; init; } = [];

    [Description("Work experience entries, most recent first.")]
    [JsonPropertyName("experiences")]
    public IReadOnlyList<WorkExperience> Experiences { get; init; } = [];

    [Description("Education entries, most recent first.")]
    [JsonPropertyName("education")]
    public IReadOnlyList<Education> Education { get; init; } = [];
}

/// <summary>
/// A single work experience entry. The nullable dates deserialize from
/// ISO-8601 strings; null endDate means the job is ongoing.
/// </summary>
sealed record WorkExperience
{
    [Description("The employer or company name.")]
    [JsonPropertyName("company")]
    public string Company { get; init; } = string.Empty;

    [Description("The job title held at this company.")]
    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [Description("Start date as ISO-8601 (yyyy-MM-dd); null if unknown.")]
    [JsonPropertyName("startDate")]
    public DateTime? StartDate { get; init; }

    [Description("End date as ISO-8601 (yyyy-MM-dd); null when the job is ongoing.")]
    [JsonPropertyName("endDate")]
    public DateTime? EndDate { get; init; }

    [Description("A short description of the role and its achievements.")]
    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;
}

/// <summary>
/// A single education entry.
/// </summary>
sealed record Education
{
    [Description("The school, college, or university name.")]
    [JsonPropertyName("institution")]
    public string Institution { get; init; } = string.Empty;

    [Description("The degree or qualification earned, e.g. 'M.Sc. Computer Science'.")]
    [JsonPropertyName("degree")]
    public string Degree { get; init; } = string.Empty;

    [Description("The graduation year; null if not stated.")]
    [JsonPropertyName("graduationYear")]
    public int? GraduationYear { get; init; }
}
