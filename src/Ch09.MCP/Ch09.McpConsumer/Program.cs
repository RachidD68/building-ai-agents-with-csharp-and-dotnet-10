// ============================================================================
// Chapter 9 — The Model Context Protocol (MCP Consumer)
// Connects to the Ch09.McpServer via stdio, discovers its tools, and feeds
// them to a ChatClientAgent. The agent code is identical to Chapter 8 —
// the only difference is WHERE the tools come from (MCP server vs. inline).
//
// Key concepts:
//   - StdioClientTransport to launch an MCP server as a child process
//   - McpClient.CreateAsync() to connect and negotiate capabilities
//   - ListToolsAsync() to discover tools from the server
//   - McpClientTool extends AITool, so it plugs directly into the agent
// ============================================================================

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using AgenticAI.Common.Extensions;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);

using IHost host = builder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();

Console.WriteLine("=== Chapter 9: MCP Consumer — Discovering Tools from an MCP Server ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// ---------------------------------------------------------------
// Step 1: Connect to the MCP server via stdio transport
// ---------------------------------------------------------------
// The StdioClientTransport launches the MCP server as a child process.
// The server's stdin/stdout become the communication channel.
string serverProjectPath = Path.GetFullPath(
    Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Ch09.McpServer"));

Console.WriteLine($"Launching MCP server from: {serverProjectPath}");

StdioClientTransport transport = new(new StdioClientTransportOptions
{
    Command = "dotnet",
    Arguments = ["run", "--project", serverProjectPath, "--no-build"],
    Name = "ContosoProductServer"
});

await using McpClient mcpClient = await McpClient.CreateAsync(
    transport,
    cancellationToken: cts.Token);

Console.WriteLine($"Connected to: {mcpClient.ServerInfo?.Name} v{mcpClient.ServerInfo?.Version}");

// ---------------------------------------------------------------
// Step 2: Discover tools from the MCP server
// ---------------------------------------------------------------
IList<McpClientTool> mcpTools = await mcpClient.ListToolsAsync(
    cancellationToken: cts.Token);

Console.WriteLine($"\nDiscovered {mcpTools.Count} tools from MCP server:");
foreach (McpClientTool tool in mcpTools)
{
    Console.WriteLine($"  - {tool.Name}: {tool.Description}");
}

Console.WriteLine();

// ---------------------------------------------------------------
// Step 3: Feed MCP tools to the ChatClientAgent
// ---------------------------------------------------------------
// McpClientTool extends AITool, so it plugs directly into the agent's
// tools parameter — identical to how we registered inline tools in Ch08.
ChatClientAgent agent = new(
    chatClient,
    instructions: "You are a product assistant. Use the available tools to look up products and check inventory. Be helpful and concise.",
    name: "McpProductAssistant",
    description: "A product assistant powered by tools from an MCP server",
    tools: [.. mcpTools]);

// ---------------------------------------------------------------
// Step 4: Use the agent — identical to Chapter 8
// ---------------------------------------------------------------
string[] queries =
[
    "Look up product 1003 and tell me about it.",
    "Check the inventory for SKU KB-ERG-001.",
    "Is the webcam pro in stock? What's its price?"
];

foreach (string query in queries)
{
    AgentSession session = await agent.CreateSessionAsync(cts.Token);
    Console.WriteLine($"You: {query}");

    try
    {
        AgentResponse response = await agent.RunAsync(
            query,
            session,
            cancellationToken: cts.Token);

        Console.WriteLine($"Agent: {response.Text}\n");
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("\n[Cancelled by user]");
        return;
    }
}

Console.WriteLine("=== MCP Consumer demo complete! ===");
