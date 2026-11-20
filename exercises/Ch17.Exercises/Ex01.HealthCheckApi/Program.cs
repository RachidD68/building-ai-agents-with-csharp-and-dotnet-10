// ============================================================================
// Exercise 01 — Health Check API (Basic)
// Liveness and readiness are two questions with two endpoints:
//   GET /health/live    is the process up? Runs only "self". Never calls the
//                       model, so an orchestrator polling it costs no tokens.
//   GET /health/ready   can it serve? Checks the session-store dependency.
//                       Also no model call: a readiness probe that spends
//                       tokens on every poll is a bill, not a check.
//   POST /chat          the agent (the model is called here, and only here)
//
//   dotnet run                    Ollama by default (LlmProvider section)
//   dotnet run -- --self-test     TestServer + a counting fake model, offline:
//                                 100 liveness probes make 0 model calls; a failed
//                                 store turns readiness Unhealthy (503), not liveness
// ============================================================================

using System.Collections.Concurrent;
using AgenticAI.Common.Extensions;
using Ex01.HealthCheckApi;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Diagnostics.HealthChecks;

if (args.Contains("--self-test")) return await HealthSelfTest.RunAsync();

HealthApi.Build(WebApplication.CreateBuilder(args), chatClient: null).Run();
return 0;

namespace Ex01.HealthCheckApi
{
    public static class HealthApi
    {
        public static WebApplication Build(WebApplicationBuilder builder, IChatClient? chatClient)
        {
            if (chatClient is not null) builder.Services.AddSingleton(chatClient);
            else builder.Services.AddLlmChatClient(builder.Configuration);
            builder.Services.AddSingleton(sp => new ChatClientAgent(sp.GetRequiredService<IChatClient>(),
                instructions: "You are a helpful .NET assistant. Be concise.", name: "HealthyAgent"));
            builder.Services.AddSingleton<SessionStore>();

            builder.Services.AddHealthChecks()
                .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"])
                .AddCheck<SessionStoreHealthCheck>("session-store", tags: ["ready"]);

            WebApplication app = builder.Build();
            app.MapHealthChecks("/health/live", new HealthCheckOptions
            {
                Predicate = check => check.Tags.Contains("live"), ResponseWriter = WriteJsonAsync,
            });
            app.MapHealthChecks("/health/ready", new HealthCheckOptions
            {
                Predicate = check => check.Tags.Contains("ready"), ResponseWriter = WriteJsonAsync,
            });
            app.MapPost("/chat", async (ChatClientAgent agent, ChatRequest request, CancellationToken ct) =>
            {
                AgentResponse response = await agent.RunAsync(request.Message, await agent.CreateSessionAsync(ct), cancellationToken: ct);
                return Results.Ok(new ChatReply(agent.Name ?? "HealthyAgent", response.Text));
            });
            return app;
        }

        private static Task WriteJsonAsync(HttpContext context, HealthReport report)
        {
            context.Response.ContentType = "application/json";
            return context.Response.WriteAsJsonAsync(new
            {
                status = report.Status.ToString(),
                checks = report.Entries.Select(e => new { name = e.Key, status = e.Value.Status.ToString(), e.Value.Description }),
            });
        }
    }

    /// <summary>Readiness: a write/read round trip against the store. No model call.</summary>
    public sealed class SessionStoreHealthCheck(SessionStore store) : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            string key = $"probe-{Guid.NewGuid():N}";
            bool ok = store.TryPut(key, "probe") && store.TryGet(key, out string? value) && value == "probe";
            store.Remove(key);
            return Task.FromResult(ok
                ? HealthCheckResult.Healthy($"round trip OK ({store.Count} sessions held)")
                : HealthCheckResult.Unhealthy("session store refused the round trip"));
        }
    }

    public sealed record ChatRequest(string Message);

    public sealed record ChatReply(string Agent, string Response);

    /// <summary>In-memory stand-in for an external session store. <see cref="Down"/> simulates an outage.</summary>
    public sealed class SessionStore
    {
        private readonly ConcurrentDictionary<string, string> _entries = new();

        public bool Down { get; set; }

        public int Count => _entries.Count;

        public bool TryPut(string key, string value)
        {
            if (Down) return false;
            _entries[key] = value;
            return true;
        }

        public bool TryGet(string key, out string? value)
        {
            value = null;
            return !Down && _entries.TryGetValue(key, out value);
        }

        public void Remove(string key) => _entries.TryRemove(key, out _);
    }
}
