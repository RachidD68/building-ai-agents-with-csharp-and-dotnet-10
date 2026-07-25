// ============================================================================
// Exercise 02 — Session Manager (Intermediate)
// A small SessionManager keeping multiple NAMED sessions, each persisted to
// its own JSON file under a configurable folder, with /new, /switch, /list,
// and /delete commands in a looped console chat. Persistence is REAL:
// SerializeSessionAsync after every turn, DeserializeSessionAsync when
// switching to a name that only exists on disk — so any conversation resumes
// after an app restart. FULLY STANDALONE.
//
// Serialization needs the framework's source-generated resolver: copy
// AgentAbstractionsJsonUtilities.DefaultOptions into the JsonSerializerOptions.
// A bare new JsonSerializerOptions() throws NotSupportedException on the
// first save.
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

// ---------------------------------------------------------------
// Configurable session folder (SessionManager:Directory in appsettings)
// ---------------------------------------------------------------
string sessionsDir = Path.Combine(
    Environment.CurrentDirectory,
    config["SessionManager:Directory"] ?? "sessions");
Directory.CreateDirectory(sessionsDir);

// Resolver-backed options — see the header note.
JsonSerializerOptions jsonOptions =
    new(AgentAbstractionsJsonUtilities.DefaultOptions) { WriteIndented = true };

// The history provider is wired explicitly so the transcript rides inside
// every serialized session payload.
InMemoryChatHistoryProvider historyProvider = new(new InMemoryChatHistoryProviderOptions());

ChatClientAgentOptions agentOptions = new()
{
    Name = "SessionAgent",
    Description = "An assistant whose conversations persist per named session",
    ChatHistoryProvider = historyProvider,
    ChatOptions = new ChatOptions
    {
        Instructions = "You are a helpful assistant. Be concise."
    }
};

ChatClientAgent agent = new(chatClient, options: agentOptions);

// The manager: named sessions loaded in memory (the hint's Dictionary).
Dictionary<string, AgentSession> sessions = [];
string? currentSessionName = null;

string[] onDisk = ListSavedSessionNames();
if (onDisk.Length > 0)
{
    Console.WriteLine($"Saved sessions found on disk: {string.Join(", ", onDisk)}");
    Console.WriteLine("Use /switch <name> to resume one — its conversation survives restarts.\n");
}

Console.WriteLine($"Session folder: {sessionsDir}");
Console.WriteLine("Commands: /new <name>, /switch <name>, /list, /delete <name>, /quit");
Console.WriteLine("Or just type a message to chat in the current session.\n");

while (!cts.Token.IsCancellationRequested)
{
    string prefix = currentSessionName is not null ? $"[{currentSessionName}] You" : "[no session] You";
    Console.Write($"{prefix}: ");
    string? input = Console.ReadLine()?.Trim();

    // ReadLine returns null at end-of-input (Ctrl+Z, or redirected stdin
    // running dry). Treating null like an empty line spins this loop forever;
    // it means "no more input is ever coming", so leave.
    if (input is null) break;
    if (input.Length == 0) continue;

    if (input.Equals("/quit", StringComparison.OrdinalIgnoreCase)) break;

    if (input.StartsWith("/new ", StringComparison.OrdinalIgnoreCase))
    {
        string name = input[5..].Trim();
        if (!IsValidSessionName(name))
        {
            Console.WriteLine("  Usage: /new <session-name> (letters, digits, - and _ only)\n");
            continue;
        }

        if (sessions.ContainsKey(name) || File.Exists(SessionFilePath(name)))
        {
            Console.WriteLine($"  Session '{name}' already exists. Use /switch {name} to resume it.\n");
            continue;
        }

        AgentSession created = await agent.CreateSessionAsync(cts.Token);
        sessions[name] = created;
        currentSessionName = name;

        // Persist immediately so the session exists on disk from birth.
        await SaveSessionAsync(name, created);
        Console.WriteLine($"  Created session '{name}' (saved to {SessionFilePath(name)})\n");
        continue;
    }

    if (input.StartsWith("/switch ", StringComparison.OrdinalIgnoreCase))
    {
        string name = input[8..].Trim();
        if (!IsValidSessionName(name))
        {
            Console.WriteLine("  Usage: /switch <session-name> (letters, digits, - and _ only)\n");
            continue;
        }

        if (sessions.ContainsKey(name))
        {
            currentSessionName = name;
            Console.WriteLine($"  Switched to session '{name}'.\n");
        }
        else if (File.Exists(SessionFilePath(name)))
        {
            // Resume from disk: DeserializeSessionAsync rebuilds the session —
            // including its conversation history — from the JSON payload.
            string savedJson = await File.ReadAllTextAsync(SessionFilePath(name), cts.Token);
            JsonElement state = JsonSerializer.Deserialize<JsonElement>(savedJson);

            AgentSession restored = await agent.DeserializeSessionAsync(state, jsonOptions, cts.Token);
            sessions[name] = restored;
            currentSessionName = name;

            int restoredMessages = historyProvider.GetMessages(restored).Count;
            Console.WriteLine($"  Resumed session '{name}' from disk ({restoredMessages} messages restored).\n");
        }
        else
        {
            // Load-or-create: the file doesn't exist yet — don't assume it does.
            AgentSession created = await agent.CreateSessionAsync(cts.Token);
            sessions[name] = created;
            currentSessionName = name;

            await SaveSessionAsync(name, created);
            Console.WriteLine($"  No saved session '{name}' — created a new one.\n");
        }
        continue;
    }

    if (input.Equals("/list", StringComparison.OrdinalIgnoreCase))
    {
        string[] names = [.. sessions.Keys.Union(ListSavedSessionNames(), StringComparer.OrdinalIgnoreCase).Order()];
        if (names.Length == 0)
        {
            Console.WriteLine("  No sessions. Use /new <name> to create one.\n");
        }
        else
        {
            Console.WriteLine("  Sessions:");
            foreach (string name in names)
            {
                string marker = name == currentSessionName
                    ? " (current)"
                    : sessions.ContainsKey(name) ? " (loaded)" : " (on disk)";
                Console.WriteLine($"    - {name}{marker}");
            }
            Console.WriteLine();
        }
        continue;
    }

    if (input.StartsWith("/delete ", StringComparison.OrdinalIgnoreCase))
    {
        string name = input[8..].Trim();
        bool removedFromMemory = sessions.Remove(name);
        bool removedFromDisk = false;

        if (IsValidSessionName(name) && File.Exists(SessionFilePath(name)))
        {
            File.Delete(SessionFilePath(name));
            removedFromDisk = true;
        }

        if (removedFromMemory || removedFromDisk)
        {
            if (currentSessionName == name) currentSessionName = null;
            Console.WriteLine($"  Deleted session '{name}'.\n");
        }
        else
        {
            Console.WriteLine($"  Session '{name}' not found.\n");
        }
        continue;
    }

    // ---------------------------------------------------------------
    // Regular chat message — run the turn, then persist the session
    // ---------------------------------------------------------------
    if (currentSessionName is null || !sessions.TryGetValue(currentSessionName, out AgentSession? session))
    {
        Console.WriteLine("  No active session. Use /new <name> or /switch <name> first.\n");
        continue;
    }

    try
    {
        AgentResponse response = await agent.RunAsync(
            input, session, cancellationToken: cts.Token);

        Console.WriteLine($"  Assistant: {response.Text}\n");

        // Persist after every turn: kill the app any time, resume any time.
        await SaveSessionAsync(currentSessionName, session);
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("\n[Cancelled]");
        break;
    }
}

Console.WriteLine($"\nSessions directory: {sessionsDir}");
Console.WriteLine("Run the app again and /switch to any saved session to continue it.");
Console.WriteLine("=== Exercise complete! ===");

// ---------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------

// Serializes the session (conversation included) to its own JSON file.
async Task SaveSessionAsync(string name, AgentSession session)
{
    JsonElement state = await agent.SerializeSessionAsync(session, jsonOptions, cts.Token);
    string json = JsonSerializer.Serialize(state, jsonOptions);
    await File.WriteAllTextAsync(SessionFilePath(name), json, cts.Token);
}

string SessionFilePath(string name) => Path.Combine(sessionsDir, $"{name}.json");

string[] ListSavedSessionNames() =>
    [.. Directory.GetFiles(sessionsDir, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>()];

// Keeps names file-system safe (and blocks path traversal in file names).
static bool IsValidSessionName(string name) =>
    name.Length > 0 && name.All(c => char.IsLetterOrDigit(c) || c is '-' or '_');

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
