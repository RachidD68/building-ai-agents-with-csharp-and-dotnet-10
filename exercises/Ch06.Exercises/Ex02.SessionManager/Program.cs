// ============================================================================
// Exercise 02 — Session Manager (Intermediate)
// Console app supporting multiple named sessions. Users can create, switch,
// list, and delete sessions. Sessions persist to JSON files. FULLY STANDALONE.
// ============================================================================

using System.ClientModel;
using System.Text.Json;
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

Console.WriteLine("=== Exercise: Session Manager ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

string sessionsDir = Path.Combine(Environment.CurrentDirectory, "sessions");
Directory.CreateDirectory(sessionsDir);

ChatClientAgent agent = new(
    chatClient,
    instructions: "You are a helpful assistant. Be concise.",
    name: "SessionAgent");

// Track active sessions (name → session)
Dictionary<string, AgentSession> sessions = [];
string? currentSessionName = null;

Console.WriteLine("Commands: /new <name>, /switch <name>, /list, /delete <name>, /quit");
Console.WriteLine("Or just type a message to chat in the current session.\n");

while (true)
{
    string prefix = currentSessionName is not null ? $"[{currentSessionName}] You" : "[no session] You";
    Console.Write($"{prefix}: ");
    string? input = Console.ReadLine()?.Trim();

    if (string.IsNullOrEmpty(input)) continue;

    if (input.Equals("/quit", StringComparison.OrdinalIgnoreCase)) break;

    if (input.StartsWith("/new ", StringComparison.OrdinalIgnoreCase))
    {
        string name = input[5..].Trim();
        if (string.IsNullOrEmpty(name))
        {
            Console.WriteLine("  Usage: /new <session-name>\n");
            continue;
        }

        AgentSession newSession = await agent.CreateSessionAsync(cts.Token);
        sessions[name] = newSession;
        currentSessionName = name;
        await SaveSessionInfoAsync(sessionsDir, name, cts.Token);
        Console.WriteLine($"  Created and switched to session: {name}\n");
        continue;
    }

    if (input.StartsWith("/switch ", StringComparison.OrdinalIgnoreCase))
    {
        string name = input[8..].Trim();
        if (sessions.ContainsKey(name))
        {
            currentSessionName = name;
            Console.WriteLine($"  Switched to session: {name}\n");
        }
        else
        {
            Console.WriteLine($"  Session '{name}' not found. Use /list to see available sessions.\n");
        }
        continue;
    }

    if (input.Equals("/list", StringComparison.OrdinalIgnoreCase))
    {
        if (sessions.Count == 0)
        {
            Console.WriteLine("  No sessions. Use /new <name> to create one.\n");
        }
        else
        {
            Console.WriteLine("  Active sessions:");
            foreach (string name in sessions.Keys)
            {
                string marker = name == currentSessionName ? " (current)" : "";
                Console.WriteLine($"    - {name}{marker}");
            }
            Console.WriteLine();
        }
        continue;
    }

    if (input.StartsWith("/delete ", StringComparison.OrdinalIgnoreCase))
    {
        string name = input[8..].Trim();
        if (sessions.Remove(name))
        {
            string filePath = Path.Combine(sessionsDir, $"{name}.json");
            if (File.Exists(filePath)) File.Delete(filePath);

            if (currentSessionName == name) currentSessionName = null;
            Console.WriteLine($"  Deleted session: {name}\n");
        }
        else
        {
            Console.WriteLine($"  Session '{name}' not found.\n");
        }
        continue;
    }

    // Regular chat message
    if (currentSessionName is null || !sessions.TryGetValue(currentSessionName, out AgentSession? session))
    {
        Console.WriteLine("  No active session. Use /new <name> to create one.\n");
        continue;
    }

    try
    {
        AgentResponse response = await agent.RunAsync(
            input, session, cancellationToken: cts.Token);

        Console.WriteLine($"  Assistant: {response.Text}\n");
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("\n[Cancelled]");
        break;
    }
}

Console.WriteLine($"\nSessions directory: {sessionsDir}");
Console.WriteLine("=== Exercise complete! ===");

// ---------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------

/// <summary>
/// Saves session metadata to a JSON file.
/// </summary>
static async Task SaveSessionInfoAsync(
    string dir, string name, CancellationToken cancellationToken)
{
    var info = new { Name = name, CreatedAt = DateTime.Now.ToString("O") };
    string json = JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true });
    await File.WriteAllTextAsync(
        Path.Combine(dir, $"{name}.json"), json, cancellationToken);
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
