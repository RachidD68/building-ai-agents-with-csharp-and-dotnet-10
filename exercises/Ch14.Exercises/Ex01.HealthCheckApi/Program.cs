// ============================================================================
// Exercise 01 — Health Check API (Beginner)
// Web API hosting an agent with TWO custom IHealthCheck implementations:
//   "chat-client"   → verifies the IChatClient answers a minimal test prompt
//                     (low MaxOutputTokens keeps the probe cost bounded)
//   "session-store" → verifies a mock session-store dependency round-trips
// Registered via AddHealthChecks().AddCheck<T>("name"), exposed at /healthz.
// FULLY STANDALONE.
// ============================================================================

using System.ClientModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Diagnostics.HealthChecks;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IChatClient>(sp =>
    CreateChatClient(sp.GetRequiredService<IConfiguration>()));

builder.Services.AddSingleton(sp =>
    new ChatClientAgent(
        sp.GetRequiredService<IChatClient>(),
        instructions: "You are a helpful .NET assistant. Be concise.",
        name: "HealthyAgent",
        description: "An agent with health monitoring"));

// The mock dependency the second health check probes.
builder.Services.AddSingleton<MockSessionStore>();

// Register both custom checks. Each name becomes one entry in the /healthz
// report — and one row in the Aspire dashboard when hosted under an AppHost.
builder.Services.AddHealthChecks()
    .AddCheck<ChatClientHealthCheck>("chat-client")
    .AddCheck<SessionStoreHealthCheck>("session-store");

WebApplication app = builder.Build();

// /healthz runs every registered check. The JSON response writer is optional —
// the default writer returns plain-text "Healthy" — but per-check detail makes
// the probe results visible with a simple curl.
app.MapHealthChecks("/healthz", new HealthCheckOptions
{
    ResponseWriter = WriteJsonResponseAsync
});

app.MapPost("/chat", async (
    ChatClientAgent agent,
    ChatRequest request,
    CancellationToken ct) =>
{
    AgentSession session = await agent.CreateSessionAsync(ct);
    AgentResponse response = await agent.RunAsync(request.Message, session, cancellationToken: ct);
    return Results.Ok(new ChatReply(agent.Name ?? "HealthyAgent", response.Text ?? string.Empty));
})
.WithName("Chat");

Console.WriteLine("=== Exercise: Health Check API ===");
Console.WriteLine("Endpoints: GET /healthz, POST /chat");

app.Run();

// ---------------------------------------------------------------
// Health response writer (optional JSON detail)
// ---------------------------------------------------------------

/// <summary>Writes the health report as JSON with one entry per check.</summary>
static Task WriteJsonResponseAsync(HttpContext context, HealthReport report)
{
    context.Response.ContentType = "application/json";
    return context.Response.WriteAsJsonAsync(new
    {
        status = report.Status.ToString(),
        totalDurationMs = report.TotalDuration.TotalMilliseconds,
        checks = report.Entries.Select(entry => new
        {
            name = entry.Key,
            status = entry.Value.Status.ToString(),
            durationMs = entry.Value.Duration.TotalMilliseconds,
            description = entry.Value.Description
        })
    });
}

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
// Health checks
// ---------------------------------------------------------------

/// <summary>
/// Verifies the chat client is reachable by sending a minimal test prompt.
/// MaxOutputTokens is kept tiny so the liveness probe costs almost nothing.
/// </summary>
sealed class ChatClientHealthCheck(IChatClient chatClient) : IHealthCheck
{
    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            Stopwatch sw = Stopwatch.StartNew();
            await chatClient.GetResponseAsync(
                "Reply with OK.",
                new ChatOptions { MaxOutputTokens = 5 },
                cancellationToken);
            sw.Stop();

            return HealthCheckResult.Healthy(
                $"Chat client answered the test prompt in {sw.ElapsedMilliseconds} ms.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Chat client is unreachable.", ex);
        }
    }
}

/// <summary>
/// Verifies the mock session-store dependency accepts a write and reads back
/// the same value — the stand-in for a real Redis/Cosmos session store.
/// </summary>
sealed class SessionStoreHealthCheck(MockSessionStore store) : IHealthCheck
{
    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        string probeKey = $"healthz-{Guid.NewGuid():N}";

        store.Put(probeKey, "probe");
        bool roundTripped = store.TryGet(probeKey, out string? value) && value == "probe";
        store.Remove(probeKey);

        return Task.FromResult(roundTripped
            ? HealthCheckResult.Healthy($"Session store round-trip OK ({store.Count} sessions held).")
            : HealthCheckResult.Unhealthy("Session store failed the write/read round-trip."));
    }
}

// ---------------------------------------------------------------
// Types
// ---------------------------------------------------------------

/// <summary>Chat request payload.</summary>
record ChatRequest(string Message);

/// <summary>Chat response payload.</summary>
record ChatReply(string Agent, string Response);

/// <summary>In-memory stand-in for an external session store.</summary>
sealed class MockSessionStore
{
    private readonly ConcurrentDictionary<string, string> _entries = new();

    /// <summary>Number of entries currently stored.</summary>
    public int Count => _entries.Count;

    /// <summary>Stores a value under the given key.</summary>
    public void Put(string key, string value) => _entries[key] = value;

    /// <summary>Attempts to read a value back.</summary>
    public bool TryGet(string key, out string? value) => _entries.TryGetValue(key, out value);

    /// <summary>Removes an entry if present.</summary>
    public void Remove(string key) => _entries.TryRemove(key, out _);
}
