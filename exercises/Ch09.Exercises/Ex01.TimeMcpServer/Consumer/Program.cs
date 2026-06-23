// ============================================================================
// Exercise 01 — Time MCP Consumer
// Connects to the Time MCP server, discovers tools, and uses them via an agent.
// FULLY STANDALONE.
// ============================================================================

using System.ClientModel;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

using IHost host = builder.Build();
IConfiguration config = host.Services.GetRequiredService<IConfiguration>();
IChatClient chatClient = CreateChatClient(config);

Console.WriteLine("=== Exercise: Time MCP Consumer ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// Connect to the MCP server
string serverProjectPath = Path.GetFullPath(
    Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Server"));

Console.WriteLine($"Launching MCP server from: {serverProjectPath}");

StdioClientTransport transport = new(new StdioClientTransportOptions
{
    Command = "dotnet",
    Arguments = ["run", "--project", serverProjectPath, "--no-build"],
    Name = "TimeServer"
});

await using McpClient mcpClient = await McpClient.CreateAsync(
    transport, cancellationToken: cts.Token);

Console.WriteLine($"Connected to: {mcpClient.ServerInfo?.Name}");

IList<McpClientTool> tools = await mcpClient.ListToolsAsync(cancellationToken: cts.Token);
Console.WriteLine($"Discovered {tools.Count} tools:\n");
foreach (McpClientTool tool in tools)
{
    Console.WriteLine($"  - {tool.Name}: {tool.Description}");
}

// Create agent with MCP tools
ChatClientAgent agent = new(
    chatClient,
    instructions: "You are a helpful time assistant. Use the available tools for time queries.",
    name: "TimeAssistant",
    tools: [.. tools]);

string[] queries =
[
    "What time is it right now in Tokyo?",
    "If it's 2:30 PM in New York, what time is it in London?",
    "What's the current time in UTC and Pacific time?"
];

foreach (string query in queries)
{
    Console.WriteLine($"\nYou: {query}");

    try
    {
        AgentSession session = await agent.CreateSessionAsync(cts.Token);
        AgentResponse response = await agent.RunAsync(
            query, session, cancellationToken: cts.Token);

        Console.WriteLine($"Agent: {response.Text}");
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("\n[Cancelled]");
        return;
    }
}

Console.WriteLine("\n=== Exercise complete! ===");

// ---------------------------------------------------------------
// Provider factory
// ---------------------------------------------------------------

/// <summary>Creates an IChatClient from configuration.</summary>
static IChatClient CreateChatClient(IConfiguration configuration)
{
    string provider = configuration["LlmProvider:Provider"]?.ToLowerInvariant() ?? "github";

    return provider switch
    {
        "azure" => new AzureOpenAIClient(
                new Uri(configuration["LlmProvider:AzureOpenAI:Endpoint"] ?? throw new InvalidOperationException("Missing endpoint")),
                new DefaultAzureCredential())
            .GetChatClient(configuration["LlmProvider:AzureOpenAI:Model"] ?? "gpt-4o-mini").AsIChatClient(),
        "openai" => new OpenAI.OpenAIClient(configuration["LlmProvider:OpenAI:ApiKey"] ?? throw new InvalidOperationException("Missing key"))
            .GetChatClient(configuration["LlmProvider:OpenAI:Model"] ?? "gpt-4o-mini").AsIChatClient(),
        "github" => new OpenAI.OpenAIClient(
                new ApiKeyCredential(configuration["LlmProvider:GitHub:Token"] ?? throw new InvalidOperationException("Missing token")),
                new OpenAI.OpenAIClientOptions { Endpoint = new Uri(configuration["LlmProvider:GitHub:Endpoint"] ?? "https://models.github.ai/inference") })
            .GetChatClient(configuration["LlmProvider:GitHub:Model"] ?? "openai/gpt-4o-mini").AsIChatClient(),
        "ollama" => new OllamaSharp.OllamaApiClient(
            new Uri(configuration["LlmProvider:Ollama:Endpoint"] ?? "http://localhost:11434"),
            configuration["LlmProvider:Ollama:Model"] ?? "llama3.2"),
        _ => throw new InvalidOperationException($"Unknown provider: '{provider}'")
    };
}
