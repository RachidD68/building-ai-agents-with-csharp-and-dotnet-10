// ============================================================================
// Exercise 01 — Persona Switcher (Basic)
// Type a persona description (e.g., "sarcastic support engineer") followed by
// a question. A FRESH ChatClientAgent is spun up with those instructions for
// each question — the IChatClient is reused across calls; only the agent
// changes. FULLY STANDALONE.
//
// Note: the instructions constructor accepts any string, so the user's text
// goes into the system prompt verbatim. That is an injection vector — fine
// for this exercise; Chapter 16 shows how to sanitize.
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

// One IChatClient for the whole run — reused by every agent we create below.
IChatClient chatClient = CreateChatClient(config);

Console.WriteLine("=== Exercise: Persona Switcher ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine("Describe a persona, then ask it a question.");
Console.WriteLine("A fresh agent is created with your persona for every question.");
Console.WriteLine("Example persona: sarcastic support engineer");
Console.WriteLine("Leave the persona blank (or type 'quit') to exit.\n");

while (!cts.Token.IsCancellationRequested)
{
    Console.Write("Persona: ");
    string? persona = Console.ReadLine()?.Trim();

    if (string.IsNullOrEmpty(persona) ||
        persona.Equals("quit", StringComparison.OrdinalIgnoreCase))
    {
        break;
    }

    Console.Write("Question: ");
    string? question = Console.ReadLine()?.Trim();

    if (string.IsNullOrEmpty(question))
    {
        Console.WriteLine("  (No question — try again.)\n");
        continue;
    }

    // A fresh agent per question: same chat client, new instructions.
    // The persona description the user typed IS the system prompt.
    ChatClientAgent agent = new(
        chatClient,
        instructions: persona,
        name: "PersonaAgent",
        description: $"An agent acting as: {persona}");

    try
    {
        // Single-turn call — no session needed; MAF creates a throwaway
        // session internally just for this run.
        AgentResponse response = await agent.RunAsync(
            question,
            cancellationToken: cts.Token);

        Console.WriteLine($"\n[{persona}]: {response.Text}\n");
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("\n[Cancelled]");
        break;
    }
}

Console.WriteLine("=== Exercise complete! ===");

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
                new Uri(configuration["LlmProvider:AzureOpenAI:Endpoint"] ?? throw new InvalidOperationException("Missing AzureOpenAI:Endpoint")),
                new DefaultAzureCredential())
            .GetChatClient(configuration["LlmProvider:AzureOpenAI:Model"] ?? "gpt-4o-mini")
            .AsIChatClient(),

        "openai" => new OpenAI.OpenAIClient(
                configuration["LlmProvider:OpenAI:ApiKey"] ?? throw new InvalidOperationException("Missing OpenAI:ApiKey"))
            .GetChatClient(configuration["LlmProvider:OpenAI:Model"] ?? "gpt-4o-mini")
            .AsIChatClient(),

        "github" => new OpenAI.OpenAIClient(
                new ApiKeyCredential(configuration["LlmProvider:GitHub:Token"] ?? throw new InvalidOperationException("Missing GitHub:Token")),
                new OpenAI.OpenAIClientOptions { Endpoint = new Uri(configuration["LlmProvider:GitHub:Endpoint"] ?? "https://models.github.ai/inference") })
            .GetChatClient(configuration["LlmProvider:GitHub:Model"] ?? "openai/gpt-4o-mini")
            .AsIChatClient(),

        "ollama" => new OllamaSharp.OllamaApiClient(
            new Uri(configuration["LlmProvider:Ollama:Endpoint"] ?? "http://localhost:11434"),
            configuration["LlmProvider:Ollama:Model"] ?? "llama3.2"),

        _ => throw new InvalidOperationException($"Unknown provider: '{provider}'")
    };
}
