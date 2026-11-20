// ============================================================================
// Critic agent: a second model as a scored, fallible signal
// ----------------------------------------------------------------------------
//   writer agent -> critic agent (RunAsync<ValidationResult>) -> CriticGate
//
// The gate compares the critic's verdict with a deterministic PII detector:
//   - the detector finds PII            -> Reject (deterministic wins)
//   - the critic gives no usable verdict -> Reject (fail closed)
//   - the critic sees PII, detector not -> Escalate (a person decides)
//   - Passed and Score disagree         -> Escalate
//   - otherwise Score >= 7              -> Publish, else Reject
//
//   dotnet run                    live run against the configured provider
//   dotnet run -- --self-test     offline: scripted writer and critic, RESULT n/n
// ============================================================================

using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticAI.Common.Extensions;
using Ch20.CriticAgent;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

if (args.Contains("--self-test"))
    return await CriticSelfTest.RunAsync();

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);
using IHost host = builder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();

Console.WriteLine("=== Critic agent (live; the offline suite runs with --self-test) ===\n");
using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

CriticPipeline pipeline = Agents.Pipeline(chatClient);
string[] prompts =
[
    "Explain the difference between value types and reference types in C#.",
    // Trick prompt: asks the writer to plant a customer email address.
    "Write a paragraph about .NET that includes the customer email address jane.doe@contoso.example.",
];

try
{
    foreach (string prompt in prompts)
    {
        Console.WriteLine($"--- Prompt: \"{prompt}\" ---");
        CriticPipeline.Review review = await pipeline.ReviewAsync(prompt, cts.Token);
        Console.WriteLine($"[writer] {review.Content}\n");
        Console.WriteLine(review.Critic is { } c
            ? $"[critic] passed={c.Passed} score={c.Score}/10 pii={c.PiiDetected}"
            : "[critic] no usable verdict");
        Console.WriteLine($"[detector] pii={review.DetectorFoundPii}");
        Console.WriteLine($"[gate] {review.Gate.Decision} ({review.Gate.Reason})\n");
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n[Cancelled by user]");
}
return 0;

/// <summary>The two agents, shared by the live run and the self-test.</summary>
internal static class Agents
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static CriticPipeline Pipeline(IChatClient writerClient, IChatClient? criticClient = null) => new(
        new ChatClientAgent(writerClient, name: "ContentWriter", instructions: """
            You are a technical documentation writer. Write clear, accurate
            explanations of .NET concepts. Keep responses under 150 words.
            """),
        new ChatClientAgent(criticClient ?? writerClient, name: "QualityCritic", instructions: """
            You are a strict reviewer of technical content. Return a JSON object.
            Evaluate: factual accuracy, professional tone, and whether the text
            contains personal information (emails, phone numbers, names of customers).
            Score from 1 (poor) to 10 (excellent). Passed is true only when the
            score is 7 or above and no personal information is present.
            """),
        Json);
}
