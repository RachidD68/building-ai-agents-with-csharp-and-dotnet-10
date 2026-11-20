// ============================================================================
// Exercise 02 — Resume Parser (Intermediate, solution)
// Extracts a Resume record from pasted resume text: candidate name, contact
// info (email AND phone), skills, experiences as WorkExperience records with
// DateTime? start/end dates, and a list of Education entry records. DateTime?
// covers fields that may be missing — a current job has no end date. Under
// strict mode the nullable means "the key is always present and may be null";
// [JsonRequired] on EndDate enforces the same on our side, so a reply that
// omits the key, or writes "Present" or "June 2021", is a Failure, never a
// default date. Failures use the chapter's TryRunStructuredAsync<T>.
//
//   dotnet run                    one resume through the model
//   dotnet run -- --self-test     offline checks, fake chat client, RESULT n/n
// ============================================================================

using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticAI.Common.Extensions;
using Ch05.RobustExtraction;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

JsonSerializerOptions jsonOptions = new(JsonSerializerOptions.Web);
const string Instructions = """
    You are a resume parser. Extract structured data from resume text.
    Return ONLY valid JSON matching the requested schema.
    Dates must be ISO-8601 (yyyy-MM-dd). When only a year is given, use
    January 1 of that year. For ongoing jobs ("Present"), set endDate to
    null — include the key, do not omit it.
    For lists, return an empty array if no items are found.
    """;

if (args.Contains("--self-test")) return await SelfTestAsync();

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);
using IHost host = builder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();
ILogger logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ResumeParser");

Console.WriteLine("=== Exercise: Resume Parser ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

ChatClientAgent agent = new(chatClient, instructions: Instructions, name: "ResumeParser");

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
    Result<Resume> result = await agent.TryRunStructuredAsync<Resume>(
        $"Parse this resume:\n\n{sampleResume}", jsonOptions, logger, cts.Token);

    if (result is Failure<Resume> failure)
    {
        Console.WriteLine($"--- No resume: {string.Join("; ", failure.Messages)}");
    }
    else if (result is Success<Resume> { Value: Resume resume })
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
return 0;

// ---------------------------------------------------------------
// Offline checks: canned replies through the chapter's fake chat client.
// ---------------------------------------------------------------
async Task<int> SelfTestAsync()
{
    Console.WriteLine("=== Resume Parser — self-test (offline) ===\n");
    int passed = 0, total = 0;
    async Task<Result<Resume>> Parse(string endDateMember)
    {
        string json = $$"""
            {"name":"Jane Smith","email":"jane.smith@techcorp.dev","phone":"+1 (206) 555-0142","skills":["C#"],
             "experiences":[{"company":"TechCorp Inc.","title":"Senior Software Engineer","startDate":"2021-06-01"{{endDateMember}},"description":"Cloud migration."}],
             "education":[{"institution":"MIT","degree":"M.Sc. Computer Science","graduationYear":2016}]}
            """;
        CannedChatClient client = new(CannedChatClient.Stop(json));
        ChatClientAgent fake = new(client, instructions: Instructions);
        return await fake.TryRunStructuredAsync<Resume>("Parse this resume.", jsonOptions, NullLogger.Instance);
    }
    void Check(string name, bool ok)
    {
        total++; passed += ok ? 1 : 0;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    }

    Check("the current job, \"endDate\": null with the key present -> EndDate == null",
        await Parse(""","endDate":null""") is Success<Resume> { Value.Experiences: [{ EndDate: null, StartDate.Year: 2021 }] });
    Check("a past job, \"endDate\": \"2021-06-01\" -> EndDate is that date",
        await Parse(""","endDate":"2021-06-01" """) is Success<Resume> { Value.Experiences: [{ EndDate.Month: 6 }] });
    Check("the endDate key omitted -> Failure, not a silent \"ongoing\"",
        await Parse("") is Failure<Resume>);
    Check("a malformed date \"June 2021\" -> Failure, not a default date",
        await Parse(""","endDate":"June 2021" """) is Failure<Resume>);
    Check("\"Present\" written as a date -> Failure",
        await Parse(""","endDate":"Present" """) is Failure<Resume>);

    Console.WriteLine($"\nRESULT {passed}/{total}");
    return passed == total ? 0 : 1;
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

    // Required but nullable: the key must be present; null means "ongoing".
    [Description("End date as ISO-8601 (yyyy-MM-dd); null when the job is ongoing.")]
    [JsonPropertyName("endDate")]
    [JsonRequired]
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
