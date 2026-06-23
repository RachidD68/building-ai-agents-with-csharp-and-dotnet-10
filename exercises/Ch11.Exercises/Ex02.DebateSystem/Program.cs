// ============================================================================
// Exercise 02 — Debate System (Intermediate)
// Two agents debate a topic. A JudgeAgent scores each round. FULLY STANDALONE.
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

Console.WriteLine("=== Exercise: Debate System ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

string topic = "Should all software be open source?";

ChatClientAgent proAgent = new(chatClient,
    instructions: $"""
        You are a passionate debater arguing FOR the position. The topic is: "{topic}"
        Make compelling arguments with examples. Keep each response to 3-4 sentences.
        Address your opponent's previous arguments when applicable.
        """,
    name: "ProDebater");

ChatClientAgent conAgent = new(chatClient,
    instructions: $"""
        You are a passionate debater arguing AGAINST the position. The topic is: "{topic}"
        Make compelling counterarguments with examples. Keep each response to 3-4 sentences.
        Address your opponent's previous arguments when applicable.
        """,
    name: "ConDebater");

ChatClientAgent judge = new(chatClient,
    instructions: """
        You are an impartial debate judge. Score each round on a scale of 1-10 for each debater.
        Consider argument strength, evidence, and rhetorical skill.
        Format: "Pro: X/10, Con: Y/10. Brief explanation." Keep it to 2 sentences.
        """,
    name: "Judge");

Console.WriteLine($"Topic: {topic}\n");
Console.WriteLine(new string('=', 60));

int proTotal = 0, conTotal = 0;
string previousArgument = $"Opening statement on: {topic}";

try
{
    for (int round = 1; round <= 3; round++)
    {
        Console.WriteLine($"\n--- Round {round} ---\n");

        // Pro argues
        AgentSession proSession = await proAgent.CreateSessionAsync(cts.Token);
        AgentResponse proResponse = await proAgent.RunAsync(
            $"Round {round}. Previous argument from opponent: {previousArgument}",
            proSession, cancellationToken: cts.Token);
        string proText = proResponse.Text ?? string.Empty;
        Console.WriteLine($"  [PRO] {proText}\n");

        // Con argues
        AgentSession conSession = await conAgent.CreateSessionAsync(cts.Token);
        AgentResponse conResponse = await conAgent.RunAsync(
            $"Round {round}. Previous argument from opponent: {proText}",
            conSession, cancellationToken: cts.Token);
        string conText = conResponse.Text ?? string.Empty;
        Console.WriteLine($"  [CON] {conText}\n");

        previousArgument = conText;

        // Judge scores
        AgentSession judgeSession = await judge.CreateSessionAsync(cts.Token);
        AgentResponse judgeResponse = await judge.RunAsync(
            $"Round {round} debate on '{topic}':\nPro: {proText}\nCon: {conText}\n\nScore this round.",
            judgeSession, cancellationToken: cts.Token);
        Console.WriteLine($"  [JUDGE] {judgeResponse.Text}");

        // Simple score extraction (just add round number as proxy if parsing fails)
        proTotal += round;
        conTotal += round;
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n[Cancelled]");
}

Console.WriteLine($"\n{new string('=', 60)}");
Console.WriteLine("Debate complete! See judge's per-round scores above.");
Console.WriteLine("\n=== Exercise complete! ===");

// ---------------------------------------------------------------
// Provider factory
// ---------------------------------------------------------------

/// <summary>Creates an IChatClient from configuration.</summary>
static IChatClient CreateChatClient(IConfiguration configuration)
{
    string provider = configuration["LlmProvider:Provider"]?.ToLowerInvariant() ?? "github";
    return provider switch
    {
        "azure" => new AzureOpenAIClient(new Uri(configuration["LlmProvider:AzureOpenAI:Endpoint"] ?? throw new InvalidOperationException("Missing endpoint")), new DefaultAzureCredential())
            .GetChatClient(configuration["LlmProvider:AzureOpenAI:Model"] ?? "gpt-4o-mini").AsIChatClient(),
        "openai" => new OpenAI.OpenAIClient(configuration["LlmProvider:OpenAI:ApiKey"] ?? throw new InvalidOperationException("Missing key"))
            .GetChatClient(configuration["LlmProvider:OpenAI:Model"] ?? "gpt-4o-mini").AsIChatClient(),
        "github" => new OpenAI.OpenAIClient(new ApiKeyCredential(configuration["LlmProvider:GitHub:Token"] ?? throw new InvalidOperationException("Missing token")),
                new OpenAI.OpenAIClientOptions { Endpoint = new Uri(configuration["LlmProvider:GitHub:Endpoint"] ?? "https://models.github.ai/inference") })
            .GetChatClient(configuration["LlmProvider:GitHub:Model"] ?? "openai/gpt-4o-mini").AsIChatClient(),
        "ollama" => new OllamaSharp.OllamaApiClient(new Uri(configuration["LlmProvider:Ollama:Endpoint"] ?? "http://localhost:11434"), configuration["LlmProvider:Ollama:Model"] ?? "llama3.2"),
        _ => throw new InvalidOperationException($"Unknown provider: '{provider}'")
    };
}
