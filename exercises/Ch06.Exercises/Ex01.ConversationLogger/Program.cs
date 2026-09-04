// ============================================================================
// Exercise 01 — Conversation Logger (Basic)
// Extends Ch06.AgentSessionDemo: the agent runs with a wired
// InMemoryChatHistoryProvider, and EVERY turn is appended to a log file in
// real time. Each log line carries a timestamp, the SESSION ID, the role, and
// the message text. The log file survives across runs; the in-memory history
// does not. FULLY STANDALONE.
//
// AgentSession has no built-in id in MAF (ChatClientAgentSession.ConversationId
// stays null for client-side history), so the app assigns each session a short
// id of its own — which is exactly what makes the log readable: two sessions,
// two ids, one file.
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

// A FIXED file name, appended to on every run — that is what makes the log
// survive across runs while the in-memory history does not.
string logFile = Path.Combine(Environment.CurrentDirectory, "conversation.log");

if (File.Exists(logFile))
{
    int previousLines = File.ReadAllLines(logFile).Length;
    Console.WriteLine($"Log file already holds {previousLines} lines from previous runs.");
}
Console.WriteLine($"Logging to: {logFile}\n");

// ---------------------------------------------------------------
// The agent, with the history provider actually wired
// ---------------------------------------------------------------
InMemoryChatHistoryProvider historyProvider = new(new InMemoryChatHistoryProviderOptions());

ChatClientAgentOptions agentOptions = new()
{
    Name = "MemoryBot",
    Description = "An assistant that remembers the conversation — and logs it",
    ChatHistoryProvider = historyProvider,
    ChatOptions = new ChatOptions
    {
        Temperature = 0.7f
    }
};

ChatClientAgent agent = new(chatClient, options: agentOptions);

try
{
    // ---------------------------------------------------------------
    // Session 1 — the three AgentSessionDemo turns, each logged live
    // ---------------------------------------------------------------
    AgentSession session = await agent.CreateSessionAsync(cts.Token);
    string sessionId = NewSessionId();

    Console.WriteLine($"Session {sessionId} created. Every turn is logged as it happens.\n");

    await RunLoggedTurnAsync(session, sessionId,
        "My name is Rachid. I live in Seattle and I love C# programming.");

    await RunLoggedTurnAsync(session, sessionId,
        "What's my name? Where do I live? What programming language do I love?");

    await RunLoggedTurnAsync(session, sessionId,
        "Based on what you know about me, suggest a weekend project.");

    // ---------------------------------------------------------------
    // Session 2 — isolation: a fresh session, a fresh id, same log file
    // ---------------------------------------------------------------
    Console.WriteLine(new string('=', 60));
    Console.WriteLine("SESSION ISOLATION");
    Console.WriteLine(new string('=', 60));

    AgentSession session2 = await agent.CreateSessionAsync(cts.Token);
    string sessionId2 = NewSessionId();

    Console.WriteLine($"New session {sessionId2} — asking 'What is my name?'\n");

    await RunLoggedTurnAsync(session2, sessionId2, "What is my name?");

    Console.WriteLine("As expected, the new session has no memory of the first conversation —");
    Console.WriteLine("but the log file recorded both sessions, each line tagged with its id.\n");

    // ---------------------------------------------------------------
    // In-memory history vs the log file
    // ---------------------------------------------------------------
    int session1Messages = historyProvider.GetMessages(session).Count;
    int session2Messages = historyProvider.GetMessages(session2).Count;
    int logLines = File.ReadAllLines(logFile).Length;

    Console.WriteLine(new string('=', 60));
    Console.WriteLine($"In-memory history: {session1Messages} messages ({sessionId}), " +
                      $"{session2Messages} messages ({sessionId2}) — gone when the process exits.");
    Console.WriteLine($"Log file:          {logLines} lines total — survives the next run.");
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n[Cancelled]");
}

Console.WriteLine("\n=== Exercise complete! ===");

// ---------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------

// Runs one turn: logs the user message, runs the agent, logs the reply.
// Both log writes happen in real time, before the next turn starts.
async Task RunLoggedTurnAsync(AgentSession session, string sessionId, string message)
{
    Console.WriteLine($"You: {message}");
    await LogTurnAsync(logFile, sessionId, "user", message, cts.Token);

    AgentResponse response = await agent.RunAsync(message, session, cancellationToken: cts.Token);

    Console.WriteLine($"Agent: {response.Text}\n");
    await LogTurnAsync(logFile, sessionId, "assistant", response.Text ?? string.Empty, cts.Token);
}

// A short app-assigned session id (see the header note).
static string NewSessionId() => Guid.NewGuid().ToString("N")[..8];

/// <summary>
/// Appends one log line: timestamp, session id, role, message text.
/// </summary>
/// <param name="path">The log file path.</param>
/// <param name="sessionId">The id of the session the turn belongs to.</param>
/// <param name="role">The message role (user, assistant).</param>
/// <param name="text">The message text (flattened to a single line).</param>
/// <param name="cancellationToken">Token to cancel the operation.</param>
static async Task LogTurnAsync(
    string path, string sessionId, string role, string text, CancellationToken cancellationToken)
{
    string flattened = text.ReplaceLineEndings(" ");
    string entry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{sessionId}] [{role,-9}] {flattened}\n";
    await File.AppendAllTextAsync(path, entry, cancellationToken);
}

/// <summary>
/// Creates an <see cref="IChatClient"/> from User Secrets configuration.
/// </summary>
static IChatClient CreateChatClient(IConfiguration configuration)
{
    string provider = configuration["LlmProvider:Provider"]?.ToLowerInvariant() ?? "ollama";

    return provider switch
    {
        "azure" => new AzureOpenAIClient(
                new Uri(configuration["LlmProvider:AzureOpenAI:Endpoint"] ?? throw new InvalidOperationException("Missing endpoint")),
                new DefaultAzureCredential())
            .GetChatClient(configuration["LlmProvider:AzureOpenAI:Model"] ?? "gpt-5.6-luna")
            .AsIChatClient(),

        "openai" => new OpenAI.OpenAIClient(
                configuration["LlmProvider:OpenAI:ApiKey"] ?? throw new InvalidOperationException("Missing key"))
            .GetChatClient(configuration["LlmProvider:OpenAI:Model"] ?? "gpt-5.6-luna")
            .AsIChatClient(),

        "ollama" => new OllamaSharp.OllamaApiClient(
            new Uri(configuration["LlmProvider:Ollama:Endpoint"] ?? "http://localhost:11434"),
            configuration["LlmProvider:Ollama:Model"] ?? "llama3.2"),

        _ => throw new InvalidOperationException($"Unknown provider: '{provider}'")
    };
}
