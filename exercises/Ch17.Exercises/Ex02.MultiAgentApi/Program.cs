// ============================================================================
// Exercise 02 — Multi-Agent API (Intermediate)
// Four keyed agents (translator, summarizer, formalizer, classifier)
// registered with builder.AddAIAgent, one discovery route, one single-turn
// route per agent and one conversational route, all behind a bearer token:
//
//   GET  /agents                                   list (anonymous)
//   POST /agents/{name}                            SINGLE-TURN; unknown name: 404
//   POST /agents/{name}/conversations              issue an id owned by (tid, sub)
//   POST /agents/{name}/conversations/{id}         CONVERSATIONAL; not yours: 404
//
//   dotnet run                    Ollama by default (LlmProvider section)
//   dotnet run -- --self-test     TestServer, minted tokens, fake model, offline
// Preview: Microsoft.Agents.AI.Hosting (AddAIAgent) is 1.28.0-preview.
// ============================================================================

using System.Collections.Concurrent;
using AgenticAI.Common.Extensions;
using Ex02.MultiAgentApi;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.AI;

if (args.Contains("--self-test")) return await MultiAgentSelfTest.RunAsync();

MultiAgentApp.Build(WebApplication.CreateBuilder(args), chatClient: null).Run();
return 0;

namespace Ex02.MultiAgentApi
{
    public static class MultiAgentApp
    {
        public static readonly IReadOnlyDictionary<string, string> Agents = new Dictionary<string, string>
        {
            ["translator"] = "Translate the given text to English. Return ONLY the translation.",
            ["summarizer"] = "Condense the given text into 2-3 sentences. Return ONLY the summary.",
            ["formalizer"] = "Rewrite the given text in a formal business tone. Return ONLY the rewritten text.",
            ["classifier"] = "Classify the text: sentiment (positive/negative/neutral), a one- or two-word topic, one sentence of justification.",
        };

        public static WebApplication Build(WebApplicationBuilder builder, IChatClient? chatClient)
        {
            if (chatClient is not null) builder.Services.AddSingleton(chatClient);
            else builder.Services.AddLlmChatClient(builder.Configuration);
            foreach ((string name, string instructions) in Agents) builder.AddAIAgent(name, instructions);
            builder.Services.AddSingleton<Conversations>();

            builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
            builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, jwt => jwt.MapInboundClaims = false);
            builder.Services.AddAuthorization();

            WebApplication app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();

            app.MapGet("/agents", () => Results.Ok(Agents.Keys.Order().Select(n => new { name = n, route = $"POST /agents/{n}" })));

            app.MapPost("/agents/{name}", async (string name, TextRequest request, IServiceProvider sp, CancellationToken ct) =>
            {
                if (sp.GetKeyedService<AIAgent>(name) is not { } agent) return Results.NotFound();
                AgentResponse response = await agent.RunAsync(request.Text, await agent.CreateSessionAsync(ct), cancellationToken: ct);
                return Results.Ok(new AgentReply(name, null, response.Text));
            }).RequireAuthorization();

            app.MapPost("/agents/{name}/conversations", async (string name, HttpContext http, IServiceProvider sp,
                Conversations conversations, CancellationToken ct) =>
            {
                if (sp.GetKeyedService<AIAgent>(name) is not { } agent) return Results.NotFound();
                if (Owner.From(http) is not { } owner) return Results.Forbid();
                string id = Guid.NewGuid().ToString("N");
                conversations.Add(new(owner, name, id), await agent.CreateSessionAsync(ct));
                return Results.Created($"/agents/{name}/conversations/{id}", new { conversationId = id });
            }).RequireAuthorization();

            app.MapPost("/agents/{name}/conversations/{id}", async (string name, string id, TextRequest request,
                HttpContext http, IServiceProvider sp, Conversations conversations, CancellationToken ct) =>
            {
                if (sp.GetKeyedService<AIAgent>(name) is not { } agent) return Results.NotFound();
                if (Owner.From(http) is not { } owner) return Results.Forbid();
                // The caller is part of the key: another user's id finds nothing.
                if (!conversations.TryGet(new(owner, name, id), out AgentSession? session)) return Results.NotFound();
                AgentResponse response = await agent.RunAsync(request.Text, session, cancellationToken: ct);
                return Results.Ok(new AgentReply(name, id, response.Text));
            }).RequireAuthorization();
            return app;
        }
    }

    /// <summary>(tenant, subject) from the validated token.</summary>
    public sealed record Owner(string Tenant, string Subject)
    {
        public static Owner? From(HttpContext http) =>
            http.User.FindFirst("tid")?.Value is { Length: > 0 } t && http.User.FindFirst("sub")?.Value is { Length: > 0 } s
                ? new Owner(t, s) : null;
    }

    public sealed record ConversationKey(Owner Owner, string Agent, string Id);

    /// <summary>In-memory conversations, keyed by owner, agent and id. One process only.</summary>
    public sealed class Conversations
    {
        private readonly ConcurrentDictionary<ConversationKey, AgentSession> _sessions = new();

        public void Add(ConversationKey key, AgentSession session) => _sessions[key] = session;

        public bool TryGet(ConversationKey key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out AgentSession? session) =>
            _sessions.TryGetValue(key, out session);
    }

    public sealed record TextRequest(string Text);

    public sealed record AgentReply(string Agent, string? ConversationId, string Text);
}
