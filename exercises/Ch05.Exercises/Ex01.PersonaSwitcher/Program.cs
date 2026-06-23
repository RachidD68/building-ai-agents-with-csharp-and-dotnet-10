// ============================================================================
// Exercise 01 — Persona Switcher (Beginner)
// Create three agents with different personas (Chef, Detective, Poet).
// Let user pick one and chat. FULLY STANDALONE.
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

Console.WriteLine("=== Exercise: Persona Switcher ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// ---------------------------------------------------------------
// Define personas
// ---------------------------------------------------------------
Dictionary<string, (string Name, string Instructions)> personas = new()
{
    ["1"] = ("Chef Gordon", """
        You are Chef Gordon, a passionate and dramatic celebrity chef.
        You relate everything to cooking metaphors and food analogies.
        You occasionally shout enthusiastically about the beauty of food.
        Keep answers to 3-5 sentences.
        """),
    ["2"] = ("Detective Holmes", """
        You are Detective Holmes, a brilliant but eccentric detective.
        You analyze everything with deductive reasoning and often say 'Elementary!'
        You speak in a formal Victorian style and see clues everywhere.
        Keep answers to 3-5 sentences.
        """),
    ["3"] = ("Poet Luna", """
        You are Poet Luna, a dreamy and philosophical poet.
        You speak in flowing, poetic language with occasional rhymes.
        You find beauty and metaphor in everything, even technical topics.
        Keep answers to 3-5 sentences.
        """)
};

Console.WriteLine("Choose your persona:");
Console.WriteLine("  1. Chef Gordon — dramatic culinary artist");
Console.WriteLine("  2. Detective Holmes — deductive reasoning master");
Console.WriteLine("  3. Poet Luna — philosophical dreamer");
Console.Write("\nYour choice (1-3): ");

string choice = Console.ReadLine()?.Trim() ?? "1";
if (!personas.TryGetValue(choice, out var persona))
{
    persona = personas["1"];
}

ChatClientAgent agent = new(
    chatClient,
    instructions: persona.Instructions,
    name: persona.Name,
    description: $"A chatbot with the {persona.Name} persona");

Console.WriteLine($"\nYou are now chatting with {persona.Name}!");
Console.WriteLine("Type 'quit' to exit.\n");

AgentSession session = await agent.CreateSessionAsync(cts.Token);

while (true)
{
    Console.Write("You: ");
    string? input = Console.ReadLine();

    if (string.IsNullOrWhiteSpace(input) ||
        input.Equals("quit", StringComparison.OrdinalIgnoreCase))
    {
        break;
    }

    try
    {
        AgentResponse response = await agent.RunAsync(
            input, session, cancellationToken: cts.Token);

        Console.WriteLine($"{persona.Name}: {response.Text}\n");
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
