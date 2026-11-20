// ============================================================================
// Exercise 02: Dictionary MCP Consumer
// Connects to the Dictionary MCP server and uses its tools through an agent.
//
//   dotnet run                          stdio: launches Ex02.DictionaryMcpServer
//   dotnet run -- --http [url]          streamable HTTP (default http://localhost:5001/mcp;
//                                       start the server with --http first)
//   dotnet run -- --self-test           offline: HTTP transports, SSE-pinned client,
//                                       and the challenge's scopes (DictionarySelfTest.cs)
// ============================================================================

using Ex02.DictionaryMcpConsumer;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using AgenticAI.Common.Extensions;

if (args.Contains("--self-test")) return await DictionarySelfTest.RunAsync();

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);
using IHost host = builder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();

Console.WriteLine("=== Exercise: Dictionary MCP Consumer ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

int http = Array.IndexOf(args, "--http");
IClientTransport transport = http >= 0
    ? new HttpClientTransport(new HttpClientTransportOptions
    {
        Endpoint = new Uri(http + 1 < args.Length ? args[http + 1] : "http://localhost:5001/mcp"),
        TransportMode = HttpTransportMode.StreamableHttp,   // not Sse: see the self-test
    })
    : new StdioClientTransport(new StdioClientTransportOptions
    {
        Command = "dotnet",
        Arguments = ["run", "--project", Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "Ex02.DictionaryMcpServer")), "--no-build"],
        Name = "DictionaryServer"
    });

await using McpClient mcpClient = await McpClient.CreateAsync(transport, cancellationToken: cts.Token);
IList<McpClientTool> tools = await mcpClient.ListToolsAsync(cancellationToken: cts.Token);
Console.WriteLine($"Connected to: {mcpClient.ServerInfo?.Name}; discovered {tools.Count} tools: " +
    string.Join(", ", tools.Select(t => t.Name)) + "\n");

ChatClientAgent agent = new(chatClient, instructions: DictionarySelfTest.Instructions, name: "DictionaryAssistant", tools: [.. tools]);

string[] queries =
[
    "Define 'reticent' and give me three synonyms.",
    "What does 'ephemeral' mean?",
    "Give me synonyms for 'verbose'."
];

foreach (string query in queries)
{
    Console.WriteLine($"You: {query}");
    try
    {
        AgentResponse response = await agent.RunAsync(query, await agent.CreateSessionAsync(cts.Token), cancellationToken: cts.Token);
        Console.WriteLine($"Agent: {response.Text}\n");
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("\n[Cancelled]");
        return 130;
    }
}

Console.WriteLine("=== Exercise complete! ===");
return 0;
