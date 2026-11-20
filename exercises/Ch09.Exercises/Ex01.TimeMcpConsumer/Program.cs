// ============================================================================
// Exercise 01: Time MCP Consumer
// Launches the Time MCP server as a child process (stdio), discovers its one
// tool, and hands it to an agent.
//
//   dotnet run                    live: the configured model (AgenticAI.Common)
//   dotnet run -- --self-test     offline: the real server, a scripted model
// ============================================================================

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using AgenticAI.Common.Extensions;

const string Instructions =
    "You are a helpful time assistant. Use the available tools for time queries. " +
    "If a tool reports an error, say so; never guess a time.";

if (args.Contains("--self-test")) return await SelfTestAsync();

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);
using IHost host = builder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();

Console.WriteLine("=== Exercise: Time MCP Consumer ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// The built server dll of the same configuration (bin/<Configuration>/net10.0).
string liveServerDll = Path.Combine(
    AppContext.BaseDirectory.Replace("Ex01.TimeMcpConsumer", "Ex01.TimeMcpServer"), "Ex01.TimeMcpServer.dll");
Console.WriteLine($"Launching MCP server: {Path.GetFileName(liveServerDll)}");

await using McpClient mcpClient = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
{
    Command = "dotnet",
    Arguments = [liveServerDll],
    Name = "TimeServer",
    StandardErrorLines = _ => { },
}), cancellationToken: cts.Token);

IList<McpClientTool> tools = await mcpClient.ListToolsAsync(cancellationToken: cts.Token);
Console.WriteLine($"Connected to: {mcpClient.ServerInfo?.Name}; discovered {tools.Count} tool(s):");
foreach (McpClientTool tool in tools) Console.WriteLine($"  - {tool.Name}: {tool.Description}");

ChatClientAgent agent = new(chatClient, instructions: Instructions, name: "TimeAssistant", tools: [.. tools]);

string[] queries =
[
    "What time is it in Tokyo right now?",
    "What time is it in the timezone Mars/Olympus_Mons?"
];

foreach (string query in queries)
{
    Console.WriteLine($"\nYou: {query}");
    try
    {
        AgentResponse response = await agent.RunAsync(query, await agent.CreateSessionAsync(cts.Token), cancellationToken: cts.Token);
        Console.WriteLine($"Agent: {response.Text}");
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("\n[Cancelled]");
        return 130;
    }
}

Console.WriteLine("\n=== Exercise complete! ===");
return 0;

// ---------------------------------------------------------------------------
// Offline check: the real server (its built dll) and a scripted model.
// ---------------------------------------------------------------------------
static async Task<int> SelfTestAsync()
{
    Console.WriteLine("=== Time MCP consumer: self-test ===");
    int passed = 0, total = 0;
    void Expect(string name, bool ok, string? detail = null)
    {
        total++;
        if (ok) passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
        if (detail is not null) Console.WriteLine($"        {detail}");
    }

    string serverDll = Path.Combine(
        AppContext.BaseDirectory.Replace("Ex01.TimeMcpConsumer", "Ex01.TimeMcpServer"), "Ex01.TimeMcpServer.dll");
    if (!File.Exists(serverDll))
    {
        Console.WriteLine("  FAIL  Ex01.TimeMcpServer is not built; build the solution first.\n\nRESULT 0/1");
        return 1;
    }

    await using McpClient mcp = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
    {
        Command = "dotnet",
        Arguments = [serverDll],
        Name = "TimeServer",
        StandardErrorLines = _ => { },   // the server's logs; not part of the check
    }));
    IList<McpClientTool> tools = await mcp.ListToolsAsync();
    // [McpServerTool] with no Name: the SDK derives a snake_case name from the method.
    Expect("the server exposes exactly one tool, get_current_time (from GetCurrentTime)",
        tools.Count == 1 && tools[0].Name == "get_current_time", $"tools: {string.Join(", ", tools.Select(t => t.Name))}");

    CallToolResult utc = await mcp.CallToolAsync("get_current_time", new Dictionary<string, object?> { ["timezone"] = "UTC" });
    string utcText = Text(utc);
    Expect("UTC: a normal result with today's UTC date",
        utc.IsError != true && utcText.Contains(DateTime.UtcNow.ToString("yyyy-MM-dd")), utcText);

    CallToolResult bad = await mcp.CallToolAsync("get_current_time", new Dictionary<string, object?> { ["timezone"] = "Mars/Olympus_Mons" });
    Expect("an id that is not IANA: isError = true, with the reason",
        bad.IsError == true && Text(bad).Contains("Unknown timezone"), $"isError={bad.IsError}; {Text(bad)}");

    ScriptedModel model = new();
    ChatClientAgent agent = new(model, instructions: Instructions, name: "TimeAssistant", tools: [.. tools]);
    AgentResponse tokyo = await agent.RunAsync("What time is it in Tokyo?", await agent.CreateSessionAsync());
    Expect("Tokyo: the answer carries the tool's value (+09:00), not a made-up time",
        model.LastResult is not null && tokyo.Text.Contains("+09:00") && tokyo.Text.Contains(model.LastResult), tokyo.Text);
    AgentResponse mars = await agent.RunAsync("What time is it on Mars?", await agent.CreateSessionAsync());
    Expect("Mars: the model receives the tool error and the answer reports it",
        model.LastResult?.Contains("Unknown timezone") == true && mars.Text.Contains("error"), mars.Text);

    Console.WriteLine($"\nRESULT {passed}/{total}");
    return passed == total ? 0 : 1;

    static string Text(CallToolResult r) => string.Join(" ", r.Content.OfType<TextContentBlock>().Select(t => t.Text));
}

/// <summary>
/// Scripted model: asks for get_current_time with Asia/Tokyo, or Mars/Olympus_Mons
/// when the question mentions Mars; then answers with what the tool returned.
/// </summary>
sealed class ScriptedModel : IChatClient
{
    public string? LastResult { get; private set; }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ChatMessage last = messages.Last();
        if (last.Contents.OfType<FunctionResultContent>().FirstOrDefault() is { } result)
        {
            LastResult = ToolText(result.Result);
            string answer = LastResult.Contains("Unknown timezone") ? $"The time tool returned an error: {LastResult}" : LastResult;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, answer)));
        }
        string zone = last.Text.Contains("Mars") ? "Mars/Olympus_Mons" : "Asia/Tokyo";
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("call-1", "get_current_time", new Dictionary<string, object?> { ["timezone"] = zone })])));
    }

    // Keep the text blocks whatever shape the tool result arrives in.
    static string ToolText(object? result) => result switch
    {
        CallToolResult r => string.Join(" ", r.Content.OfType<TextContentBlock>().Select(t => t.Text)),
        _ => result?.ToString() ?? "",
    };

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (ChatResponseUpdate u in (await GetResponseAsync(messages, options, cancellationToken)).ToChatResponseUpdates()) yield return u;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    public void Dispose() { }
}
