// ============================================================================
// The Model Context Protocol (MCP Consumer)
// Connects to an MCP server, discovers its tools, and feeds them to a
// ChatClientAgent. The agent code is identical to the tools chapter; the only
// difference is WHERE the tools come from (MCP server vs. inline).
//
//   dotnet run                          stdio: launches Ch09.McpServer as a child process
//   dotnet run -- --http <url>          streamable HTTP, e.g. http://localhost:5000/mcp
//                                       (start Ch09.McpHttpServer first)
//   dotnet run -- --self-test           offline: the real stdio server as a child
//                                       process, and a scripted model instead of an LLM
//
// With --http and a pre-issued MCP_ACCESS_TOKEN, the token is sent only to an
// endpoint listed in McpConsumer:AllowedServers whose protected resource
// metadata names that endpoint (McpDestinationGuard).
// ============================================================================

using Ch09.McpConsumer;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using AgenticAI.Common.Extensions;

if (args.Contains("--self-test")) return await ConsumerSelfTest.RunAsync();

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);

using IHost host = builder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();

Console.WriteLine("=== MCP Consumer: Discovering Tools from an MCP Server ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// ---------------------------------------------------------------
// Step 1: Connect to the MCP server (stdio child process, or HTTP)
// ---------------------------------------------------------------
int httpIndex = Array.IndexOf(args, "--http");
IClientTransport transport;
if (httpIndex >= 0 && httpIndex + 1 < args.Length)
{
    Uri endpoint = new(args[httpIndex + 1]);
    string? token = Environment.GetEnvironmentVariable("MCP_ACCESS_TOKEN");   // pre-issued
    HttpClientTransportOptions options;
    if (token is null)
    {
        options = new() { Endpoint = endpoint, TransportMode = HttpTransportMode.StreamableHttp };
    }
    else
    {
        // Before the token leaves: the endpoint is on the allow-list, and its public
        // metadata names that same endpoint. Either check failing ends the run here.
        McpDestinationGuard guard = new(
            builder.Configuration.GetSection("McpConsumer:AllowedServers").Get<Uri[]>() ?? []);
        using HttpClient metadataClient = new();
        try
        {
            options = await guard.CreateCheckedTransportOptionsAsync(metadataClient, endpoint, token, cts.Token);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or System.Text.Json.JsonException)
        {
            // Refused (or the metadata could not be read): no request has carried the token.
            Console.Error.WriteLine($"Refused: {ex.Message} The token was not sent.");
            return 3;
        }
    }
    Console.WriteLine($"Connecting over streamable HTTP to: {endpoint}");
    transport = new HttpClientTransport(options);
}
else
{
    // The StdioClientTransport launches the MCP server as a child process.
    // The server's stdin/stdout become the communication channel. Start the
    // built dll of the same configuration (bin/<Configuration>/net10.0):
    // "dotnet run" would pick Debug and add its own console output.
    string serverDll = Path.Combine(
        AppContext.BaseDirectory.Replace("Ch09.McpConsumer", "Ch09.McpServer"), "Ch09.McpServer.dll");
    Console.WriteLine($"Launching MCP server: {Path.GetFileName(serverDll)}");
    transport = new StdioClientTransport(new StdioClientTransportOptions
    {
        Command = "dotnet",
        Arguments = [serverDll],
        Name = "ContosoProductServer",
        StandardErrorLines = _ => { },   // the server's logs stay out of the conversation
    });
}

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
ChatClientAgent agent = CatalogAgent.Create(chatClient, mcpTools);

// ---------------------------------------------------------------
// Step 4: Use the agent, identical to the tools chapter
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
        return 130;
    }
}

Console.WriteLine("=== MCP Consumer demo complete! ===");
return 0;
