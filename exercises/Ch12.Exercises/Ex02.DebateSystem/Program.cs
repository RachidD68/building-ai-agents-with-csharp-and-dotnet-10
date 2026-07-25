// ============================================================================
// Exercise 02 — Debate System (Intermediate)
// Two agents — a Proponent and a Skeptic — debate a technical topic over
// three rounds. Both share ONE List<ChatMessage> conversation log, so every
// turn sees the full history, not just the previous argument. A Judge scores
// each round "X/10"; the scores are parsed and totalled at the end.
// FULLY STANDALONE.
// ============================================================================

using System.ClientModel;
using System.Globalization;
using System.Text.RegularExpressions;
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

string topic = "Should all new web services use microservices?";

ChatClientAgent proponent = new(chatClient,
    instructions: $"""
        You are "Proponent" in a structured technical debate. The topic is:
        "{topic}"
        You argue FOR the position. Read the full debate so far and respond
        to your opponent's latest argument with a compelling, concrete case.
        Keep each response to 3-4 sentences.
        """,
    name: "Proponent");

ChatClientAgent skeptic = new(chatClient,
    instructions: $"""
        You are "Skeptic" in a structured technical debate. The topic is:
        "{topic}"
        You challenge the position. Read the full debate so far and counter
        your opponent's latest argument with concrete objections and examples.
        Keep each response to 3-4 sentences.
        """,
    name: "Skeptic");

ChatClientAgent judge = new(chatClient,
    instructions: """
        You are an impartial debate judge. Score the round you are given on a
        1-10 scale for each debater, considering argument strength, evidence,
        and rhetorical skill. Reply in EXACTLY this format:
        Proponent: X/10, Skeptic: Y/10. <one-sentence explanation>
        """,
    name: "Judge");

Console.WriteLine($"Topic: {topic}\n");
Console.WriteLine(new string('=', 60));

// One shared conversation log — both debaters see the SAME full history.
// Each turn is appended with a speaker prefix so the transcript stays
// readable to both models (this is a hand-rolled group chat).
List<ChatMessage> conversation =
[
    new(ChatRole.User,
        $"Debate topic: {topic} Proponent argues FOR, Skeptic argues AGAINST. Proponent opens.")
];

int proponentTotal = 0;
int skepticTotal = 0;
int scoredRounds = 0;

try
{
    for (int round = 1; round <= 3; round++)
    {
        Console.WriteLine($"\n--- Round {round} ---\n");

        // Proponent speaks — receives the full shared log.
        AgentResponse proResponse = await proponent.RunAsync(
            conversation, cancellationToken: cts.Token);
        string proText = proResponse.Text ?? string.Empty;
        conversation.Add(new ChatMessage(ChatRole.User, $"Proponent: {proText}")
        {
            AuthorName = "Proponent"
        });
        Console.WriteLine($"  [PROPONENT] {proText}\n");

        // Skeptic responds — same log, now including the argument above.
        AgentResponse skepticResponse = await skeptic.RunAsync(
            conversation, cancellationToken: cts.Token);
        string skepticText = skepticResponse.Text ?? string.Empty;
        conversation.Add(new ChatMessage(ChatRole.User, $"Skeptic: {skepticText}")
        {
            AuthorName = "Skeptic"
        });
        Console.WriteLine($"  [SKEPTIC] {skepticText}\n");

        // Judge scores the round; parse the REAL "X/10" scores it produces.
        AgentSession judgeSession = await judge.CreateSessionAsync(cts.Token);
        AgentResponse judgeResponse = await judge.RunAsync(
            $"Round {round} of the debate on \"{topic}\":\n" +
            $"Proponent: {proText}\nSkeptic: {skepticText}\n\nScore this round.",
            judgeSession, cancellationToken: cts.Token);
        string verdict = judgeResponse.Text ?? string.Empty;
        Console.WriteLine($"  [JUDGE] {verdict}");

        if (TryParseScores(verdict, out int proScore, out int skepticScore))
        {
            proponentTotal += proScore;
            skepticTotal += skepticScore;
            scoredRounds++;
        }
        else
        {
            Console.WriteLine("  [JUDGE] Could not parse \"X/10\" scores — round excluded from totals.");
        }
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n[Cancelled]");
}

Console.WriteLine($"\n{new string('=', 60)}");
if (scoredRounds > 0)
{
    Console.WriteLine($"Final score over {scoredRounds} scored round(s): " +
                      $"Proponent {proponentTotal} — Skeptic {skepticTotal}");
    string outcome = proponentTotal > skepticTotal
        ? "The Proponent takes the debate!"
        : skepticTotal > proponentTotal
            ? "The Skeptic takes the debate!"
            : "A dead heat.";
    Console.WriteLine(outcome);
}
else
{
    Console.WriteLine("No rounds could be scored — check the judge's output format.");
}
Console.WriteLine($"Shared log: {conversation.Count} messages ({conversation.Count - 1} debate turns).");
Console.WriteLine("\n=== Exercise complete! ===");

// ---------------------------------------------------------------
// Score parsing
// ---------------------------------------------------------------

/// <summary>Extracts "Proponent: X/10" and "Skeptic: Y/10" from the verdict.</summary>
static bool TryParseScores(string verdict, out int proponentScore, out int skepticScore)
{
    proponentScore = 0;
    skepticScore = 0;

    Match pro = Regex.Match(verdict, @"Proponent:\s*(\d{1,2})\s*/\s*10", RegexOptions.IgnoreCase);
    Match ske = Regex.Match(verdict, @"Skeptic:\s*(\d{1,2})\s*/\s*10", RegexOptions.IgnoreCase);
    if (!pro.Success || !ske.Success)
    {
        return false;
    }

    proponentScore = int.Parse(pro.Groups[1].Value, CultureInfo.InvariantCulture);
    skepticScore = int.Parse(ske.Groups[1].Value, CultureInfo.InvariantCulture);
    return true;
}

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
