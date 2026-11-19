// ============================================================================
// Hosting and Deployment (ASP.NET Core Host)
// Demonstrates hosting multiple agents in a single ASP.NET Core application
// using the Microsoft Agent Framework Hosting package (AddAIAgent pattern),
// plus the production cross-cutting concerns the chapter teaches:
//   - SSE streaming from a keyed agent  (TypedResults.ServerSentEvents)
//   - JWT bearer authentication          (RequireAuthorization)
//   - Per-user rate limiting             (AddRateLimiter / RequireRateLimiting)
//
// Key concepts:
//   - AddAIAgent():                register agents with DI via MAF Hosting
//   - WithInMemorySessionStore():  registers a session store with the agent
//   - Keyed services:              resolve specific agents by name
//   - Minimal API endpoints:       expose agents as HTTP services
//
// Endpoints (all agent endpoints require a bearer token). Each POST is
// SINGLE-TURN: it creates a fresh AgentSession per request, so the agent does
// not remember earlier requests. A conversational endpoint would load the
// caller's session by a conversation id, run, and save it back.
//
//   POST /chat            → assistant agent  (JSON)
//   POST /chat/stream     → assistant agent  (Server-Sent Events)
//   POST /summarize       → summarizer agent (JSON)
//   GET  /agents          → list registered agents (anonymous)
//
// Local auth setup (no IdP required):
//   dotnet user-jwts create --scope "agent.invoke"
//   curl -H "Authorization: Bearer <token>" ...
// ============================================================================

using System.Runtime.CompilerServices;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Extensions.AI;
using AgenticAI.Common.Extensions;
using Ch17.AspireOrchestration.ServiceDefaults;

if (args.Contains("--self-test")) return RateLimitKeys.SelfTest();

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Service defaults: OpenTelemetry (OTLP to the Aspire dashboard when Aspire
// runs this project) and the health checks behind /health.
builder.AddServiceDefaults();

// ---------------------------------------------------------------
// Step 1: Register the IChatClient from Common configuration
// ---------------------------------------------------------------
builder.Services.AddLlmChatClient(builder.Configuration);

// ---------------------------------------------------------------
// Step 2: Register agents using the MAF Hosting AddAIAgent() API
// ---------------------------------------------------------------
// Each agent is registered as a keyed singleton in DI.
// The key is the agent name passed to AddAIAgent().

builder.AddAIAgent(
    name: "assistant",
    instructions: """
        You are a helpful AI assistant specialized in .NET and C# development.
        Provide clear, concise answers with code examples when appropriate.
        Always mention best practices and potential pitfalls.
        Keep responses under 200 words unless asked for detail.
        """)
    .WithInMemorySessionStore();

builder.AddAIAgent(
    name: "summarizer",
    instructions: """
        You are a text summarization expert. When given text, produce a concise
        summary that captures the key points. Use bullet points for clarity.
        Aim for 3-5 bullet points. Do not add information not in the original text.
        """)
    .WithInMemorySessionStore();

// ---------------------------------------------------------------
// Step 3: Authentication — protect the agent endpoints
// ---------------------------------------------------------------
// JWT bearer. For local development, mint tokens with `dotnet user-jwts`;
// the validation parameters it writes into appsettings.Development.json are
// picked up automatically. For Entra ID, swap AddJwtBearer() for
// Microsoft.Identity.Web's AddMicrosoftIdentityWebApi(builder.Configuration).
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();
builder.Services.AddAuthorization();

// ---------------------------------------------------------------
// Step 4: Rate limiting — a per-user partition, 20 requests / 10s
// ---------------------------------------------------------------
// The partition key is the authenticated subject (see RateLimitKeys at the
// bottom); unauthenticated callers fall back to the remote IP so a single
// client cannot exhaust the quota.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("per-user", httpContext =>
    {
        string partitionKey = RateLimitKeys.PartitionKey(httpContext);

        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ =>
            new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromSeconds(10),
                QueueLimit = 0
            });
    });
});

// To publish an OpenAPI document, add the Microsoft.AspNetCore.OpenApi package,
// then `builder.Services.AddOpenApi();` here and `app.MapOpenApi();` below. It is
// omitted from this sample to keep the dependency set minimal — see the chapter's
// "Documenting the API" note.

WebApplication app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// ---------------------------------------------------------------
// Step 5: Define Minimal API endpoints
// ---------------------------------------------------------------

// JSON chat — one request, one fully-formed response.
app.MapPost("/chat", async (
    ChatRequest request,
    [FromKeyedServices("assistant")] AIAgent agent,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Message))
    {
        return Results.BadRequest(new { error = "Message is required." });
    }

    AgentSession session = await agent.CreateSessionAsync(cancellationToken);

    AgentResponse response = await agent.RunAsync(
        request.Message,
        session,
        cancellationToken: cancellationToken);

    return Results.Ok(new AgentResponseDto(
        Agent: agent.Name ?? "assistant",
        Message: response.Text ?? string.Empty,
        InputTokens: (int?)response.Usage?.InputTokenCount,
        OutputTokens: (int?)response.Usage?.OutputTokenCount));
})
.RequireAuthorization()
.RequireRateLimiting("per-user");

// SSE chat — stream tokens to the client as the model produces them.
// TypedResults.ServerSentEvents writes each string as a `data:` frame under
// the supplied event type. The framework CancellationToken is honoured, so a
// client disconnect cancels the in-flight LLM call.
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

app.MapPost("/summarize", async (
    ChatRequest request,
    [FromKeyedServices("summarizer")] AIAgent agent,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Message))
    {
        return Results.BadRequest(new { error = "Text to summarize is required." });
    }

    AgentSession session = await agent.CreateSessionAsync(cancellationToken);

    AgentResponse response = await agent.RunAsync(
        $"Summarize the following text:\n\n{request.Message}",
        session,
        cancellationToken: cancellationToken);

    return Results.Ok(new AgentResponseDto(
        Agent: agent.Name ?? "summarizer",
        Message: response.Text ?? string.Empty,
        InputTokens: (int?)response.Usage?.InputTokenCount,
        OutputTokens: (int?)response.Usage?.OutputTokenCount));
})
.RequireAuthorization()
.RequireRateLimiting("per-user");

// Liveness for load balancers and the Aspire dashboard (anonymous).
app.MapHealthChecks("/health");

app.MapGet("/agents", () => Results.Ok(new
{
    agents = new[]
    {
        new { name = "assistant", endpoint = "POST /chat" },
        new { name = "assistant", endpoint = "POST /chat/stream (SSE)" },
        new { name = "summarizer", endpoint = "POST /summarize" }
    }
}));

app.MapGet("/", () => Results.Ok(new
{
    service = "Multi-Agent ASP.NET Core Host",
    endpoints = new
    {
        chat = "POST /chat { \"message\": \"...\" }  (auth)",
        chatStream = "POST /chat/stream { \"message\": \"...\" }  (auth, SSE)",
        summarize = "POST /summarize { \"message\": \"...\" }  (auth)",
        agents = "GET /agents"
    }
}));

app.Run();
return 0;

// ---------------------------------------------------------------
// DTOs
// ---------------------------------------------------------------

/// <summary>
/// Incoming chat or summarization request.
/// </summary>
/// <param name="Message">The user's message or text to process.</param>
internal sealed record ChatRequest(string Message);

/// <summary>
/// Response DTO returned from agent endpoints.
/// </summary>
/// <param name="Agent">The name of the responding agent.</param>
/// <param name="Message">The agent's response text.</param>
/// <param name="InputTokens">Input tokens consumed, if available.</param>
/// <param name="OutputTokens">Output tokens generated, if available.</param>
internal sealed record AgentResponseDto(
    string Agent,
    string Message,
    int? InputTokens,
    int? OutputTokens);

// ---------------------------------------------------------------
// Rate-limit partition key
// ---------------------------------------------------------------
// AddJwtBearer maps inbound claims by default (MapInboundClaims = true), so a
// token's "sub" arrives as ClaimTypes.NameIdentifier. Looking only for "sub"
// found nothing and put every user in their IP's partition — so two users
// behind one NAT shared a quota, and one user could not be limited alone.
internal static class RateLimitKeys
{
    public static string PartitionKey(HttpContext httpContext) =>
        httpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
        ?? httpContext.User.FindFirst("sub")?.Value
        ?? httpContext.Connection.RemoteIpAddress?.ToString()
        ?? "anonymous";

    /// <summary>Offline check: dotnet run -- --self-test</summary>
    public static int SelfTest()
    {
        static HttpContext Context(string? ip, params (string Type, string Value)[] claims)
        {
            DefaultHttpContext context = new();
            if (claims.Length > 0)
            {
                context.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                    claims.Select(c => new System.Security.Claims.Claim(c.Type, c.Value)), "test"));
            }
            if (ip is not null) context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(ip);
            return context;
        }

        string nameId = System.Security.Claims.ClaimTypes.NameIdentifier;
        (string Name, HttpContext Context, string Expected)[] cases =
        [
            ("mapped subject (the default JWT handling)", Context("10.0.0.1", (nameId, "alice")), "alice"),
            ("unmapped \"sub\" claim", Context("10.0.0.1", ("sub", "bob")), "bob"),
            ("two users behind one IP stay apart", Context("10.0.0.1", (nameId, "carol")), "carol"),
            ("anonymous caller falls back to the IP", Context("10.0.0.9"), "10.0.0.9"),
            ("no identity and no IP", Context(null), "anonymous"),
        ];
        int passed = 0;
        foreach ((string name, HttpContext context, string expected) in cases)
        {
            string actual = PartitionKey(context);
            bool ok = actual == expected;
            passed += ok ? 1 : 0;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name,-44} -> {actual}");
        }
        Console.WriteLine($"RESULT {passed}/{cases.Length}");
        return passed == cases.Length ? 0 : 1;
    }
}
