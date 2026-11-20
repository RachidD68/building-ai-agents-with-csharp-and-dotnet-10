// ============================================================================
// Orchestrator and specialists: agents as tools, with the calls counted
// ----------------------------------------------------------------------------
// An orchestrator agent routes each question to one or more specialists. Each
// specialist is exposed to it as a tool with AsAIFunction(), so the
// orchestrator's model decides which to call. That is one composition style
// among several: a workflow (BuildSequential, BuildConcurrent, Handoff) passes
// messages between agents directly, with no tool call in between.
//
// The price is visible: every question costs at least two orchestrator calls
// (choose the tools, then write the answer) on top of the specialists' calls.
// A counting client under each agent prints them per question.
//
//   User question -> TechnicalOrchestrator -> [CodeExpert | DataExpert | SecurityExpert]
// ============================================================================

using System.Collections.Concurrent;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgenticAI.Common.Extensions;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);
using IHost host = builder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();

Console.WriteLine("=== Orchestrator and specialists ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

ConcurrentDictionary<string, int> calls = new();
IChatClient Counted(string agent) => new CountingChatClient(chatClient, agent, calls);

// ── The specialists. Name and description become the tool's name and description.
ChatClientAgent codeExpert = new(Counted("CodeExpert"),
    instructions: "You are a senior software architect. Answer questions about code patterns, " +
                  "architecture and best practices in at most five sentences.",
    name: "CodeExpert",
    description: "Answers questions about software design, patterns and implementation.");
ChatClientAgent dataExpert = new(Counted("DataExpert"),
    instructions: "You are a senior data engineer. Answer questions about data pipelines, schemas, " +
                  "ETL and analytics in at most five sentences.",
    name: "DataExpert",
    description: "Answers questions about data pipelines, schemas, ETL and analytics.");
ChatClientAgent securityExpert = new(Counted("SecurityExpert"),
    instructions: "You are a senior security engineer. Answer questions about authentication, " +
                  "authorization, vulnerabilities and threat modeling in at most five sentences.",
    name: "SecurityExpert",
    description: "Answers questions about authentication, authorization and threat modeling.");

// ── The orchestrator sees each specialist as a tool with one "query" parameter.
ChatClientAgent orchestrator = new(Counted("Orchestrator"),
    instructions: """
        You are a technical team lead. Route each question to the right specialist with the
        tools provided; for a question that spans several domains, consult each of them.
        Then answer, attributing each point to the specialist who made it.
        """,
    name: "TechnicalOrchestrator",
    tools: [codeExpert.AsAIFunction(), dataExpert.AsAIFunction(), securityExpert.AsAIFunction()]);

string[] questions =
[
    "How should I structure a CQRS pattern for a microservice?",
    "Our ETL pipeline is running slow. What should I look at first?",
    "Is JWT with short-lived tokens and refresh rotation secure enough for our public API?",
    "How do I design a data pipeline that is secure end to end?",   // two domains
];

try
{
    foreach (string question in questions)
    {
        calls.Clear();
        Console.WriteLine($"You: {question}\n");
        AgentResponse response = await orchestrator.RunAsync(
            question, await orchestrator.CreateSessionAsync(cts.Token), cancellationToken: cts.Token);
        Console.WriteLine($"Orchestrator: {response.Text.Trim()}\n");
        Console.WriteLine("Model calls: " + string.Join(", ", calls.OrderBy(c => c.Key).Select(c => $"{c.Key} {c.Value}")) +
                          $" (total {calls.Values.Sum()})");
        Console.WriteLine(new string('-', 60) + "\n");
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n[Cancelled by user]");
    return;
}

Console.WriteLine("=== Done ===");

/// <summary>Counts every model round trip of one agent (tool rounds included).</summary>
sealed class CountingChatClient(IChatClient inner, string agent, ConcurrentDictionary<string, int> calls)
    : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        calls.AddOrUpdate(agent, 1, (_, n) => n + 1);
        return base.GetResponseAsync(messages, options, cancellationToken);
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        calls.AddOrUpdate(agent, 1, (_, n) => n + 1);
        return base.GetStreamingResponseAsync(messages, options, cancellationToken);
    }
}
