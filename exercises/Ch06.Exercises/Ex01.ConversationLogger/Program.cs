// ============================================================================
// Exercise 01: Conversation Logger (Basic)
// ----------------------------------------------------------------------------
// Builds on Ch06.PersistentSession: each named session is serialized to its
// own JSON file after every turn, so the conversation survives a restart. After
// each RunAsync the program reads the messages the history provider holds for
// that session and appends the ones not logged yet to that session's log file:
// one line per message, with a timestamp, the session id, the role and the text.
// One file per session, so one session's log never holds another's text.
//
//   dotnet run -- <session>       run a turn on that session (default: demo);
//                                 run it again and the log keeps growing
//   dotnet run -- --self-test     a fake IChatClient and a temp folder, a
//                                 simulated restart, no network; RESULT n/n
// ============================================================================

using System.Text.Json;
using System.Text.RegularExpressions;
using AgenticAI.Common.Extensions;
using AgenticAI.Testing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

if (args.Contains("--self-test")) return await SelfTest();

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);
using IHost host = builder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

string sessionId = args.FirstOrDefault(a => !a.StartsWith('-')) ?? "demo";
LoggedConversations conversations = new(chatClient, Path.Combine(AppContext.BaseDirectory, "conversations"));

Console.WriteLine("=== Exercise: Conversation Logger ===\n");

try
{
    (AgentSession session, bool resumed) = await conversations.LoadOrCreateAsync(sessionId, cts.Token);
    Console.WriteLine(resumed
        ? $"Session '{sessionId}' restored: {conversations.History(session).Count} messages."
        : $"Session '{sessionId}' is new.");

    string message = resumed
        ? "Can you remind me what we talked about last time?"
        : "I'm planning a trip to Lisbon and I like walking tours.";
    Console.WriteLine($"You: {message}");
    string reply = await conversations.RunTurnAsync(sessionId, session, message, cts.Token);
    Console.WriteLine($"Agent: {reply}\n");

    Console.WriteLine($"Log: {conversations.LogPath(sessionId)} ({File.ReadAllLines(conversations.LogPath(sessionId)).Length} lines)");
    Console.WriteLine("Run the program again with the same session id: the log keeps the earlier turns and grows.");
}
catch (OperationCanceledException) when (cts.IsCancellationRequested)
{
    Console.WriteLine("\n[Cancelled]");
}
return 0;

// ---------------------------------------------------------------
// Offline proof: dotnet run -- --self-test
// ---------------------------------------------------------------
static async Task<int> SelfTest()
{
    Console.WriteLine("=== Conversation Logger (self-test, fake model, no network) ===\n");
    int passed = 0, total = 0;
    void Expect(string name, bool ok) { total++; if (ok) passed++; Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}"); }

    string root = Path.Combine(Path.GetTempPath(), "ex01-conversation-logger-" + Guid.NewGuid().ToString("N")[..8]);
    try
    {
        // Process 1: two turns for alice, one for bob.
        FakeChatClient model1 = new(FakeReply.Text("Teal it is."), FakeReply.Text("Lisbon has good walking tours."),
            FakeReply.Text("Hello, bob."));
        LoggedConversations first = new(model1, root);
        (AgentSession alice, _) = await first.LoadOrCreateAsync("alice");
        await first.RunTurnAsync("alice", alice, "My favourite colour is teal.");
        await first.RunTurnAsync("alice", alice, "I'm planning a trip to Lisbon.");
        (AgentSession bob, _) = await first.LoadOrCreateAsync("bob");
        await first.RunTurnAsync("bob", bob, "Hi, I'm bob and I collect stamps.");
        string[] aliceBefore = File.ReadAllLines(first.LogPath("alice"));

        // Process 2: a new client, a new agent and a new history provider; only the files remain.
        FakeChatClient model2 = new(FakeReply.Text("Your favourite colour is teal."));
        LoggedConversations second = new(model2, root);
        (AgentSession aliceAgain, bool resumed) = await second.LoadOrCreateAsync("alice");
        int restored = second.History(aliceAgain).Count;
        await second.RunTurnAsync("alice", aliceAgain, "What is my favourite colour?");
        string[] aliceAfter = File.ReadAllLines(second.LogPath("alice"));
        string[] bobLog = File.ReadAllLines(second.LogPath("bob"));

        Console.WriteLine($"   alice restored with {restored} messages; the model was sent {model2.Calls[0].Messages.Count} on the next turn");
        Console.WriteLine("   alice.log after the restart (timestamps cut):");
        foreach (string line in aliceAfter) Console.WriteLine($"     {line[24..]}");
        Console.WriteLine("   bob.log:");
        foreach (string line in bobLog) Console.WriteLine($"     {line[24..]}");

        Expect("the session came back from its file with the earlier turns, and they reached the model",
            resumed && restored == 4 && model2.Calls[0].Messages.Count == 5);
        Expect("after the restart the log still holds the earlier lines, unchanged, and the new turn is appended",
            aliceBefore.Length == 4 && aliceAfter.Length == 6 && aliceAfter.Take(4).SequenceEqual(aliceBefore)
            && aliceAfter[4].Contains("[user     ] What is my favourite colour?")
            && aliceAfter[5].Contains("[assistant] Your favourite colour is teal."));
        Expect("every line carries a timestamp, its own session id and the role",
            aliceAfter.All(l => Regex.IsMatch(l, @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} \[alice\] \[(user     |assistant)\] "))
            && bobLog.All(l => l.Contains(" [bob] ")));
        Expect("one session's log holds no message text from the other",
            !aliceAfter.Any(l => l.Contains("stamps") || l.Contains("Hello, bob"))
            && !bobLog.Any(l => l.Contains("teal") || l.Contains("Lisbon")));
        string? rejected = null;
        try { second.LogPath("../alice"); } catch (ArgumentException ex) { rejected = ex.Message; }
        Expect("a session id that isn't a plain name is refused before it becomes a path", rejected is not null);
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    Console.WriteLine($"\nRESULT {passed}/{total}");
    return passed == total ? 0 : 1;
}

/// <summary>
/// Named conversations on disk: sessions/&lt;id&gt;.json holds the serialized session
/// (the PersistentSession pattern), logs/&lt;id&gt;.log the per-message log.
/// </summary>
internal sealed partial class LoggedConversations
{
    // Session serialization is source-generated: start from the framework's options.
    private static readonly JsonSerializerOptions s_json = new(AgentAbstractionsJsonUtilities.DefaultOptions);

    private readonly InMemoryChatHistoryProvider _history = new(new InMemoryChatHistoryProviderOptions());
    private readonly ChatClientAgent _agent;
    private readonly string _root;

    public LoggedConversations(IChatClient chatClient, string root)
    {
        _root = root;
        Directory.CreateDirectory(Path.Combine(root, "sessions"));
        Directory.CreateDirectory(Path.Combine(root, "logs"));
        _agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = "LoggedBot",
            ChatHistoryProvider = _history,
        });
    }

    public string LogPath(string sessionId) => Path.Combine(_root, "logs", Checked(sessionId) + ".log");

    private string SessionPath(string sessionId) => Path.Combine(_root, "sessions", Checked(sessionId) + ".json");

    /// <summary>The messages the history provider holds for this session.</summary>
    public List<ChatMessage> History(AgentSession session) => _history.GetMessages(session);

    public async Task<(AgentSession Session, bool Resumed)> LoadOrCreateAsync(string sessionId, CancellationToken ct = default)
    {
        string path = SessionPath(sessionId);
        if (!File.Exists(path)) return (await _agent.CreateSessionAsync(ct), false);
        JsonElement saved = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(path, ct));
        return (await _agent.DeserializeSessionAsync(saved, s_json, ct), true);
    }

    /// <summary>One turn: run, log the messages the provider now holds that aren't logged yet, save.</summary>
    public async Task<string> RunTurnAsync(string sessionId, AgentSession session, string message, CancellationToken ct = default)
    {
        AgentResponse response = await _agent.RunAsync(message, session, cancellationToken: ct);

        string logPath = LogPath(sessionId);
        int alreadyLogged = File.Exists(logPath) ? File.ReadAllLines(logPath).Length : 0;
        IEnumerable<string> lines = History(session).Skip(alreadyLogged).Select(m =>
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{sessionId}] [{m.Role.Value,-9}] {m.Text.ReplaceLineEndings(" ")}");
        await File.AppendAllLinesAsync(logPath, lines, ct);

        JsonElement state = await _agent.SerializeSessionAsync(session, s_json, ct);
        await File.WriteAllTextAsync(SessionPath(sessionId), JsonSerializer.Serialize(state, s_json), ct);
        return response.Text;
    }

    // The id becomes a file name, so only a plain name is accepted.
    private static string Checked(string sessionId) =>
        SafeId().IsMatch(sessionId) ? sessionId
            : throw new ArgumentException($"Session id '{sessionId}' must be 1-40 letters, digits or dashes.", nameof(sessionId));

    [GeneratedRegex("^[A-Za-z0-9-]{1,40}$")]
    private static partial Regex SafeId();
}
