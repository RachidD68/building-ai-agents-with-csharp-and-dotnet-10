// ============================================================================
// Exercise 04 — Containerize, Secure, and Deploy (Challenge)
// A production-shaped, deployable agent API. FULLY STANDALONE.
//
// This is the runnable base for the capstone exercise. It already wires the
// three production cross-cutting concerns the chapter teaches:
//   - JWT bearer authentication          (RequireAuthorization on /chat)
//   - Per-user rate limiting             (AddRateLimiter / RequireRateLimiting)
//   - SSE streaming from the agent        (TypedResults.ServerSentEvents)
//
// Your job (see README.md):
//   1. Publish it as a container:  dotnet publish /t:PublishContainer
//      (chiseled base, no secrets baked into any layer — inject at runtime).
//   2. Deploy the Aspire app to Azure Container Apps with `azd up`.
//   3. Confirm the deployed endpoint returns 401 without a token, 200 with one.
//
// Local auth setup (no identity provider required):
//   dotnet user-jwts create --output token
//   curl -H "Authorization: Bearer <token>" ...
// ============================================================================

using System.ClientModel;
using System.Runtime.CompilerServices;
using System.Threading.RateLimiting;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------
// Step 1: The chat client and a single keyed agent
// ---------------------------------------------------------------
builder.Services.AddSingleton<IChatClient>(sp =>
    CreateChatClient(sp.GetRequiredService<IConfiguration>()));

builder.Services.AddKeyedSingleton<AIAgent>("assistant", (sp, _) =>
    new ChatClientAgent(
        sp.GetRequiredService<IChatClient>(),
        instructions: "You are a helpful AI assistant. Be concise and accurate.",
        name: "assistant"));

// ---------------------------------------------------------------
// Step 2: Authentication — protect the agent endpoints
// ---------------------------------------------------------------
// Bare JWT bearer. For local dev, mint tokens with `dotnet user-jwts create`;
// the validation parameters it writes into appsettings.Development.json are
// picked up automatically. For Entra ID, swap AddJwtBearer() for
// Microsoft.Identity.Web's AddMicrosoftIdentityWebApi(builder.Configuration).
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();
builder.Services.AddAuthorization();

// ---------------------------------------------------------------
// Step 3: Rate limiting — a per-user partition, 20 requests / 10s
// ---------------------------------------------------------------
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("per-user", httpContext =>
    {
        string partitionKey =
            httpContext.User.FindFirst("sub")?.Value
            ?? httpContext.Connection.RemoteIpAddress?.ToString()
            ?? "anonymous";

        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ =>
            new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromSeconds(10),
                QueueLimit = 0
            });
    });
});

WebApplication app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// ---------------------------------------------------------------
// Step 4: Endpoints
// ---------------------------------------------------------------

// JSON chat — one request, one fully-formed response.
app.MapPost("/chat", async (
    ChatRequest request,
    [FromKeyedServices("assistant")] AIAgent agent,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Message))
    {
        return Results.BadRequest(new { error = "Message is required." });
    }

    AgentSession session = await agent.CreateSessionAsync(ct);
    AgentResponse response = await agent.RunAsync(request.Message, session, cancellationToken: ct);
    return Results.Ok(new ChatReply(agent.Name ?? "assistant", response.Text ?? string.Empty));
})
.RequireAuthorization()
.RequireRateLimiting("per-user");

// SSE chat — stream tokens as the model produces them. The framework
// CancellationToken is honoured, so a client disconnect cancels the LLM call.
app.MapPost("/chat/stream", (
    ChatRequest request,
    [FromKeyedServices("assistant")] AIAgent agent,
    CancellationToken cancellationToken) =>
{
    async IAsyncEnumerable<string> StreamTokensAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        AgentSession session = await agent.CreateSessionAsync(ct);

        await foreach (AgentResponseUpdate update in
            agent.RunStreamingAsync(request.Message, session, cancellationToken: ct))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                yield return update.Text;
            }
        }
    }

    return TypedResults.ServerSentEvents(
        StreamTokensAsync(cancellationToken),
        eventType: "token");
})
.RequireAuthorization()
.RequireRateLimiting("per-user");

// Liveness probe (anonymous) — process up; the platform restarts on failure.
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.MapGet("/", () => Results.Ok(new
{
    service = "Chapter 14 — Ex04: Containerized, secured, deployable agent API",
    endpoints = new
    {
        chat = "POST /chat { \"message\": \"...\" }  (auth)",
        chatStream = "POST /chat/stream { \"message\": \"...\" }  (auth, SSE)",
        health = "GET /health"
    }
}));

Console.WriteLine("=== Exercise 04: Containerize, Secure, Deploy ===");
Console.WriteLine("Endpoints: POST /chat (auth), POST /chat/stream (auth, SSE), GET /health");

app.Run();

// ---------------------------------------------------------------
// Provider factory
// ---------------------------------------------------------------

/// <summary>Creates an IChatClient from configuration (defaults to Ollama).</summary>
static IChatClient CreateChatClient(IConfiguration config)
{
    string provider = config["LlmProvider:Provider"]?.ToLowerInvariant() ?? "ollama";
    return provider switch
    {
        "azure" => new AzureOpenAIClient(new Uri(config["LlmProvider:AzureOpenAI:Endpoint"] ?? throw new InvalidOperationException("Missing endpoint")), new DefaultAzureCredential())
            .GetChatClient(config["LlmProvider:AzureOpenAI:Model"] ?? "gpt-5.6-luna").AsIChatClient(),
        "openai" => new OpenAI.OpenAIClient(config["LlmProvider:OpenAI:ApiKey"] ?? throw new InvalidOperationException("Missing key"))
            .GetChatClient(config["LlmProvider:OpenAI:Model"] ?? "gpt-5.6-luna").AsIChatClient(),
        "ollama" => new OllamaSharp.OllamaApiClient(new Uri(config["LlmProvider:Ollama:Endpoint"] ?? "http://localhost:11434"), config["LlmProvider:Ollama:Model"] ?? "llama3.2"),
        _ => throw new InvalidOperationException($"Unknown provider: '{provider}'")
    };
}

// ---------------------------------------------------------------
// DTOs
// ---------------------------------------------------------------

/// <summary>Incoming chat request.</summary>
/// <param name="Message">The user's message.</param>
record ChatRequest(string Message);

/// <summary>Chat response payload.</summary>
/// <param name="Agent">The responding agent's name.</param>
/// <param name="Message">The agent's response text.</param>
record ChatReply(string Agent, string Message);
