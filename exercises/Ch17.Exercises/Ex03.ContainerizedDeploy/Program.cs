// ============================================================================
// Exercise 03 — Containerize, Secure, and Deploy (Challenge)
// A deployable agent API: bearer authentication, a rate limit partitioned
// by (tenant, subject), and a container image with no secret in any layer.
//
//   POST /chat           single-turn, bearer token, 5 requests per minute per caller
//   GET  /health/live    no model call
//
//   dotnet run                    Ollama by default (LlmProvider section)
//   dotnet run -- --self-test     TestServer, minted tokens, fake model, offline:
//                                 two tokens from one IP are limited independently,
//                                 a wrong-audience token gets 401
//   pwsh ./check-image.ps1        publish, build the image from the published
//                                 output (no network), check the user and the
//                                 layers, run the self-test inside the image
// ============================================================================

using System.Threading.RateLimiting;
using AgenticAI.Common.Extensions;
using Ex03.ContainerizedDeploy;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.AI;

if (args.Contains("--self-test")) return await DeploySelfTest.RunAsync();

DeployApp.Build(WebApplication.CreateBuilder(args), chatClient: null).Run();
return 0;

namespace Ex03.ContainerizedDeploy
{
    public static class DeployApp
    {
        public static WebApplication Build(WebApplicationBuilder builder, IChatClient? chatClient)
        {
            if (chatClient is not null) builder.Services.AddSingleton(chatClient);
            else builder.Services.AddLlmChatClient(builder.Configuration);   // the key comes from the environment
            builder.Services.AddSingleton<AIAgent>(sp => new ChatClientAgent(sp.GetRequiredService<IChatClient>(),
                instructions: "You are a helpful AI assistant. Be concise and accurate.", name: "assistant"));

            builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
            builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, jwt =>
            {
                jwt.MapInboundClaims = false;
                jwt.TokenValidationParameters.ValidateAudience = true;
            });
            builder.Services.AddAuthorization();
            builder.Services.AddHealthChecks();

            int permits = builder.Configuration.GetValue("Quota:PermitLimit", 5);
            builder.Services.AddRateLimiter(limiter =>
            {
                limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                limiter.AddPolicy("per-caller", http => RateLimitPartition.GetFixedWindowLimiter(
                    CallerKey(http), _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = permits, Window = TimeSpan.FromMinutes(1), QueueLimit = 0,
                    }));
            });

            WebApplication app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseRateLimiter();

            app.MapPost("/chat", async (ChatRequest request, AIAgent agent, CancellationToken ct) =>
            {
                AgentResponse response = await agent.RunAsync(request.Message, await agent.CreateSessionAsync(ct), cancellationToken: ct);
                return Results.Ok(new ChatReply(response.Text));
            }).RequireAuthorization().RequireRateLimiting("per-caller");
            app.MapHealthChecks("/health/live");
            return app;
        }

        // Tenant and subject together; the address only for a caller without them.
        public static string CallerKey(HttpContext http) =>
            http.User.FindFirst("tid")?.Value is { Length: > 0 } tenant && http.User.FindFirst("sub")?.Value is { Length: > 0 } subject
                ? $"user:{tenant.Length}:{tenant}/{subject}"
                : $"ip:{http.Connection.RemoteIpAddress}";
    }

    public sealed record ChatRequest(string Message);

    public sealed record ChatReply(string Text);
}
