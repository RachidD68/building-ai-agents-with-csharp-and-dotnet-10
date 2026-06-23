// ============================================================================
// Exercise 01 — Health Check API (Beginner)
// Web API hosting an agent with a health check endpoint. FULLY STANDALONE.
// ============================================================================

using System.ClientModel;
using System.Diagnostics;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IChatClient>(sp =>
{
    IConfiguration config = sp.GetRequiredService<IConfiguration>();
    return CreateChatClient(config);
});

builder.Services.AddSingleton(sp =>
    new ChatClientAgent(
        sp.GetRequiredService<IChatClient>(),
        instructions: "You are a helpful .NET assistant. Be concise.",
        name: "HealthyAgent",
        description: "An agent with health monitoring"));

// Track metrics
builder.Services.AddSingleton<AgentMetrics>();

WebApplication app = builder.Build();

AgentMetrics metrics = app.Services.GetRequiredService<AgentMetrics>();

app.MapGet("/health", (AgentMetrics m) =>
{
    return Results.Ok(new
    {
        Status = "healthy",
        AgentName = "HealthyAgent",
        Uptime = m.Uptime.ToString(@"hh\:mm\:ss"),
        TotalRequests = m.TotalRequests,
        AverageResponseTimeMs = m.AverageResponseTimeMs
    });
})
.WithName("HealthCheck");

app.MapPost("/chat", async (
    ChatClientAgent agent,
    AgentMetrics m,
    ChatRequest request,
    CancellationToken ct) =>
{
    Stopwatch sw = Stopwatch.StartNew();
    AgentSession session = await agent.CreateSessionAsync(ct);
    AgentResponse response = await agent.RunAsync(request.Message, session, cancellationToken: ct);
    sw.Stop();

    m.RecordRequest(sw.ElapsedMilliseconds);

    return Results.Ok(new ChatReply(response.Text ?? string.Empty, sw.ElapsedMilliseconds));
})
.WithName("Chat");

Console.WriteLine("=== Exercise: Health Check API ===");
Console.WriteLine("Endpoints: GET /health, POST /chat");

app.Run();

// ---------------------------------------------------------------
// Provider factory
// ---------------------------------------------------------------

/// <summary>Creates an IChatClient from configuration.</summary>
static IChatClient CreateChatClient(IConfiguration config)
{
    string provider = config["LlmProvider:Provider"]?.ToLowerInvariant() ?? "github";
    return provider switch
    {
        "azure" => new AzureOpenAIClient(new Uri(config["LlmProvider:AzureOpenAI:Endpoint"] ?? throw new InvalidOperationException("Missing endpoint")), new DefaultAzureCredential())
            .GetChatClient(config["LlmProvider:AzureOpenAI:Model"] ?? "gpt-4o-mini").AsIChatClient(),
        "openai" => new OpenAI.OpenAIClient(config["LlmProvider:OpenAI:ApiKey"] ?? throw new InvalidOperationException("Missing key"))
            .GetChatClient(config["LlmProvider:OpenAI:Model"] ?? "gpt-4o-mini").AsIChatClient(),
        "github" => new OpenAI.OpenAIClient(new ApiKeyCredential(config["LlmProvider:GitHub:Token"] ?? throw new InvalidOperationException("Missing token")),
                new OpenAI.OpenAIClientOptions { Endpoint = new Uri(config["LlmProvider:GitHub:Endpoint"] ?? "https://models.github.ai/inference") })
            .GetChatClient(config["LlmProvider:GitHub:Model"] ?? "openai/gpt-4o-mini").AsIChatClient(),
        "ollama" => new OllamaSharp.OllamaApiClient(new Uri(config["LlmProvider:Ollama:Endpoint"] ?? "http://localhost:11434"), config["LlmProvider:Ollama:Model"] ?? "llama3.2"),
        _ => throw new InvalidOperationException($"Unknown provider: '{provider}'")
    };
}

// ---------------------------------------------------------------
// Types
// ---------------------------------------------------------------

/// <summary>Chat request payload.</summary>
record ChatRequest(string Message);

/// <summary>Chat response payload.</summary>
record ChatReply(string Response, long ResponseTimeMs);

/// <summary>Simple agent metrics tracker.</summary>
sealed class AgentMetrics
{
    private readonly DateTime _startTime = DateTime.UtcNow;
    private long _totalRequests;
    private long _totalResponseTimeMs;

    /// <summary>Time since the service started.</summary>
    public TimeSpan Uptime => DateTime.UtcNow - _startTime;

    /// <summary>Total number of requests served.</summary>
    public long TotalRequests => Interlocked.Read(ref _totalRequests);

    /// <summary>Average response time in milliseconds.</summary>
    public double AverageResponseTimeMs
    {
        get
        {
            long total = Interlocked.Read(ref _totalRequests);
            return total == 0 ? 0 : (double)Interlocked.Read(ref _totalResponseTimeMs) / total;
        }
    }

    /// <summary>Records a completed request.</summary>
    /// <param name="responseTimeMs">Response time in milliseconds.</param>
    public void RecordRequest(long responseTimeMs)
    {
        Interlocked.Increment(ref _totalRequests);
        Interlocked.Add(ref _totalResponseTimeMs, responseTimeMs);
    }
}
