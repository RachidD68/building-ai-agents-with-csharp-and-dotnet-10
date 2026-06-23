// ============================================================================
// Exercise 01 — Conversation Logger (Beginner)
// Multi-turn console chat that logs every message (user + assistant) to a
// text file. Uses AgentSession for conversation memory. FULLY STANDALONE.
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

Console.WriteLine("=== Exercise: Conversation Logger ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

string logFile = Path.Combine(
    Environment.CurrentDirectory,
    $"conversation-{DateTime.Now:yyyyMMdd-HHmmss}.log");

ChatClientAgent agent = new(
    chatClient,
    instructions: "You are a helpful .NET programming assistant. Be concise.",
    name: "LoggedAssistant");

AgentSession session = await agent.CreateSessionAsync(cts.Token);

await LogMessageAsync(logFile, "SYSTEM", $"Session started at {DateTime.Now:O}", cts.Token);

Console.WriteLine($"Logging to: {logFile}");
Console.WriteLine("Type 'quit' to exit. Conversation is being logged.\n");

int turnCount = 0;

while (true)
{
    Console.Write("You: ");
    string? input = Console.ReadLine();

    if (string.IsNullOrWhiteSpace(input) ||
        input.Equals("quit", StringComparison.OrdinalIgnoreCase))
    {
        break;
    }

    turnCount++;
    await LogMessageAsync(logFile, "USER", input, cts.Token);

    try
    {
        AgentResponse response = await agent.RunAsync(
            input, session, cancellationToken: cts.Token);

        string responseText = response.Text ?? string.Empty;
        Console.WriteLine($"Assistant: {responseText}\n");

        await LogMessageAsync(logFile, "ASSISTANT", responseText, cts.Token);
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("\n[Cancelled]");
        break;
    }
}

await LogMessageAsync(logFile, "SYSTEM",
    $"Session ended. {turnCount} turns.", cts.Token);

Console.WriteLine($"\nConversation saved to: {logFile}");
Console.WriteLine($"Total turns: {turnCount}");
Console.WriteLine("=== Exercise complete! ===");

// ---------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------

/// <summary>
/// Appends a timestamped message to the log file.
/// </summary>
/// <param name="path">The log file path.</param>
/// <param name="role">The message role (USER, ASSISTANT, SYSTEM).</param>
/// <param name="content">The message content.</param>
/// <param name="cancellationToken">Token to cancel the operation.</param>
static async Task LogMessageAsync(
    string path, string role, string content, CancellationToken cancellationToken)
{
    string entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{role}] {content}\n";
    await File.AppendAllTextAsync(path, entry, cancellationToken);
}

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
