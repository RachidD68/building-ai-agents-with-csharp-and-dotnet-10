// ============================================================================
// Exercise 02 — Dictionary MCP Consumer
// Connects to the Dictionary MCP server and uses tools via an agent.
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

Console.WriteLine("=== Exercise: Dictionary MCP Consumer ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

string serverProjectPath = Path.GetFullPath(
    Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Server"));

StdioClientTransport transport = new(new StdioClientTransportOptions
{
    Command = "dotnet",
    Arguments = ["run", "--project", serverProjectPath, "--no-build"],
    Name = "DictionaryServer"
});

await using McpClient mcpClient = await McpClient.CreateAsync(
    transport, cancellationToken: cts.Token);

Console.WriteLine($"Connected to: {mcpClient.ServerInfo?.Name}");

IList<McpClientTool> tools = await mcpClient.ListToolsAsync(cancellationToken: cts.Token);
Console.WriteLine($"Discovered {tools.Count} tools\n");

ChatClientAgent agent = new(
    chatClient,
    instructions: "You are a helpful dictionary assistant. Use the tools to look up definitions and synonyms. Provide helpful context.",
    name: "DictionaryAssistant",
    tools: [.. tools]);

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
        AgentSession session = await agent.CreateSessionAsync(cts.Token);
        AgentResponse response = await agent.RunAsync(
            query, session, cancellationToken: cts.Token);
        Console.WriteLine($"Agent: {response.Text}\n");
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("\n[Cancelled]");
        return;
    }
}

Console.WriteLine("=== Exercise complete! ===");

/// <summary>Creates an IChatClient from configuration.</summary>
static IChatClient CreateChatClient(IConfiguration configuration)
{
    string provider = configuration["LlmProvider:Provider"]?.ToLowerInvariant() ?? "ollama";
    return provider switch
    {
        "azure" => new AzureOpenAIClient(
                new Uri(configuration["LlmProvider:AzureOpenAI:Endpoint"] ?? throw new InvalidOperationException("Missing endpoint")),
                new DefaultAzureCredential())
            .GetChatClient(configuration["LlmProvider:AzureOpenAI:Model"] ?? "gpt-5.6-luna").AsIChatClient(),
        "openai" => new OpenAI.OpenAIClient(configuration["LlmProvider:OpenAI:ApiKey"] ?? throw new InvalidOperationException("Missing key"))
            .GetChatClient(configuration["LlmProvider:OpenAI:Model"] ?? "gpt-5.6-luna").AsIChatClient(),
        "ollama" => new OllamaSharp.OllamaApiClient(
            new Uri(configuration["LlmProvider:Ollama:Endpoint"] ?? "http://localhost:11434"),
            configuration["LlmProvider:Ollama:Model"] ?? "llama3.2"),
        _ => throw new InvalidOperationException($"Unknown provider: '{provider}'")
    };
}
