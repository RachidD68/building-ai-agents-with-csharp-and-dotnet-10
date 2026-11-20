using System.Threading.RateLimiting;
using AgenticAI.Common.Extensions;
using Ch06.SessionOwnership;
using Ch17.AspireOrchestration.ServiceDefaults;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ch17.AspNetCoreHost;

/// <summary>What a test (or the self-test) may change. The defaults are the fixed host.</summary>
public sealed record AgentHostOptions
{
    /// <summary>null: the model comes from the LlmProvider section (AgenticAI.Common).</summary>
    public IChatClient? ChatClient { get; init; }

    /// <summary>false keeps "sub" and "tid" as they are in the token. true is the framework default.</summary>
    public bool MapInboundClaims { get; init; }

    /// <summary>The rate-limit partition of a request. A test swaps in the first edition's key.</summary>
    public Func<HttpContext, object> QuotaPartition { get; init; } = CallerIdentity.Partition;

    /// <summary>false: health checks only, no OpenTelemetry console exporter (tests).</summary>
    public bool ServiceDefaults { get; init; } = true;
}

/// <summary>The host's composition, shared by Program.cs, the self-test and Ch17.IdentityTests.</summary>
public static class AgentHost
{
    public const string AgentName = "assistant";

    public static WebApplicationBuilder Configure(WebApplicationBuilder builder, AgentHostOptions? options = null)
    {
        options ??= new AgentHostOptions();
        if (options.ServiceDefaults) builder.AddServiceDefaults();   // OpenTelemetry + the "self" liveness check
        else builder.AddDefaultHealthChecks();

        // One retry owner: AddLlmChatClient wraps every provider call in one resilience
        // handler and turns the SDK's own retries off (maxRetries: 0).
        if (options.ChatClient is { } fake) builder.Services.AddSingleton(fake);
        else builder.Services.AddLlmChatClient(builder.Configuration);

        // One receiver for the whole chapter: IHostApplicationBuilder.AddAIAgent.
        builder.AddAIAgent(AgentName, """
            You are Contoso's expense assistant. Answer questions about expense claims
            in at most three sentences. Never claim that a claim was paid or approved.
            """);

        // The conversation store is the owned store of the conversations chapter
        // (tenant + subject partitions, a concurrency token). Registering it does not
        // make a route conversational: the route below loads and saves it.
        string dbPath = builder.Configuration["Conversations:DatabasePath"]
            ?? Path.Combine(Path.GetTempPath(), "AgenticAI", "Ch17.AspNetCoreHost", "conversations.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        builder.Services.AddSingleton<AgentSessionStore>(new SqliteSessionStore(dbPath));
        builder.Services.AddHealthChecks().AddCheck<ConversationStoreCheck>("conversation-store", tags: ["ready"]);

        // Validation parameters come from Authentication:Schemes:Bearer (dotnet user-jwts
        // writes them). The audience and issuer checks are pinned on, so a missing
        // audience setting refuses every token (Ch17.IdentityTests checks it) instead of
        // depending on the handler's defaults.
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, jwt =>
        {
            jwt.MapInboundClaims = options.MapInboundClaims;
            jwt.TokenValidationParameters.ValidateAudience = true;
            jwt.TokenValidationParameters.ValidateIssuer = true;
        });
        builder.Services.AddAuthorization();

        int permits = builder.Configuration.GetValue("Quota:PermitLimit", 20);
        int window = builder.Configuration.GetValue("Quota:WindowSeconds", 10);
        builder.Services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddPolicy("per-caller", http =>
                RateLimitPartition.GetFixedWindowLimiter(options.QuotaPartition(http), _ =>
                    new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = permits, Window = TimeSpan.FromSeconds(window), QueueLimit = 0,
                    }));
        });
        return builder;
    }

    public static WebApplication Map(WebApplication app)
    {
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();

        // SINGLE-TURN: a fresh session per request, nothing stored. The model never
        // sees an earlier request, whatever the caller sends.
        app.MapPost("/ask", async (MessageRequest request,
            [FromKeyedServices(AgentName)] AIAgent agent, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Message)) return Results.BadRequest();
            AgentResponse response = await agent.RunAsync(request.Message,
                await agent.CreateSessionAsync(ct), cancellationToken: ct);
            return Results.Ok(new TurnReply(null, response.Text));
        }).RequireAuthorization().RequireRateLimiting("per-caller");

        // CONVERSATIONAL: the caller owns the conversation id the server issued.
        app.MapPost("/conversations", async (HttpContext http,
            [FromKeyedServices(AgentName)] AIAgent agent, AgentSessionStore store, CancellationToken ct) =>
        {
            if (CallerIdentity.From(http.User) is not { } caller) return Results.Forbid();
            string id = Guid.NewGuid().ToString("N");
            await store.SaveSessionAsync(agent, ConversationKeys.For(caller, id), await agent.CreateSessionAsync(ct), ct);
            return Results.Created($"/conversations/{id}", new { conversationId = id });
        }).RequireAuthorization().RequireRateLimiting("per-caller");

        app.MapPost("/conversations/{conversationId}/messages", async (string conversationId,
            MessageRequest request, HttpContext http,
            [FromKeyedServices(AgentName)] AIAgent agent, AgentSessionStore store, CancellationToken ct) =>
        {
            if (CallerIdentity.From(http.User) is not { } caller) return Results.Forbid();
            if (string.IsNullOrWhiteSpace(request.Message)) return Results.BadRequest();
            AgentSessionStoreKey key = ConversationKeys.For(caller, conversationId);
            // Load authorized: the key carries the caller's tenant and subject, so another
            // caller's id finds nothing, and "not yours" looks the same as "no such id".
            if (await store.GetSessionAsync(agent, key, ct) is not { } session) return Results.NotFound();
            AgentResponse response = await agent.RunAsync(request.Message, session, cancellationToken: ct);
            try
            {
                await store.SaveSessionAsync(agent, key, session, ct);   // save versioned
            }
            catch (SessionConflictException)
            {
                return Results.Conflict(new { error = "another turn was saved first; reload and resend" });
            }
            return Results.Ok(new TurnReply(conversationId, response.Text));
        }).RequireAuthorization().RequireRateLimiting("per-caller");

        app.MapDelete("/conversations/{conversationId}", async (string conversationId, HttpContext http,
            [FromKeyedServices(AgentName)] AIAgent agent, AgentSessionStore store, CancellationToken ct) =>
        {
            if (CallerIdentity.From(http.User) is not { } caller) return Results.Forbid();
            await store.DeleteSessionAsync(agent, ConversationKeys.For(caller, conversationId), ct);
            return Results.NoContent();
        }).RequireAuthorization().RequireRateLimiting("per-caller");

        // Liveness never calls the model; readiness checks the store, still not the model.
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = c => c.Tags.Contains("live") });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });

        app.MapGet("/agents", () => Results.Ok(new[]
        {
            new { agent = AgentName, route = "POST /ask", kind = "single-turn" },
            new { agent = AgentName, route = "POST /conversations/{id}/messages", kind = "conversational" },
        }));
        return app;
    }
}

public sealed record MessageRequest(string Message);

public sealed record TurnReply(string? ConversationId, string Text);

/// <summary>Readiness: the conversation store answers a query. It costs no tokens.</summary>
internal sealed class ConversationStoreCheck(AgentSessionStore store) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            long rows = ((SqliteSessionStore)store).Count();
            return Task.FromResult(HealthCheckResult.Healthy($"{rows} conversation(s)"));
        }
        catch (Exception e)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("conversation store unavailable", e));
        }
    }
}
