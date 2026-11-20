// ============================================================================
// Ch17.A2APreview: one agent exposed over A2A, behind the same bearer token
// as the rest of the API. Message mode only: the agent answers with a
// message, never a task (the A2A hosting of MAF 1.28 has no run-mode option).
//
//   GET  /.well-known/agent-card.json      the card (anonymous: discovery)
//   POST /a2a/expense-assistant            JSON-RPC binding          (bearer)
//   POST /a2a/expense-assistant/...        HTTP+JSON binding routes  (bearer)
//
//   dotnet run                    Ollama by default (LlmProvider section)
//   dotnet run -- --self-test     TestServer + a fake model, offline: the
//                                 mapped routes, the card's shape, 401 without
//                                 a token, a message reply over both bindings
//
// Preview: Microsoft.Agents.AI.Hosting.A2A(.AspNetCore) 1.28.0-preview and the
// A2A SDK 1.0.0-preview4; AddA2AServer and MapA2A* are experimental (MEAI001).
//
// Authentication only: the bindings need a valid token, but this sample has no
// identity-key check and no per-caller quota (Ch17.AspNetCoreHost has both).
// Message mode keeps no conversation between calls, so there is none to isolate.
// ============================================================================

using A2A;
using A2A.AspNetCore;
using AgenticAI.Common.Extensions;
using Ch17.A2APreview;
using Microsoft.Agents.AI.Hosting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.AI;

if (args.Contains("--self-test")) return await A2ASelfTest.RunAsync();

WebApplication app = A2AHost.Build(WebApplication.CreateBuilder(args), chatClient: null);
app.Run();
return 0;

namespace Ch17.A2APreview
{
    public static class A2AHost
    {
        public const string AgentName = "expense-assistant";
        public const string Path = "/a2a/expense-assistant";

        public static WebApplication Build(WebApplicationBuilder builder, IChatClient? chatClient)
        {
            if (chatClient is not null) builder.Services.AddSingleton(chatClient);
            else builder.Services.AddLlmChatClient(builder.Configuration);

            IHostedAgentBuilder agent = builder.AddAIAgent(AgentName, """
                You are Contoso's expense assistant. Answer questions about expense
                policy in at most three sentences.
                """).AddA2AServer();

            builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
            builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme,
                jwt => jwt.MapInboundClaims = false);
            builder.Services.AddAuthorization();

            WebApplication app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();

            // The card is public, so a caller can discover the agent and learn that it
            // needs a bearer token. The two bindings are not.
            app.MapWellKnownAgentCard(Card(builder.Configuration["PublicBaseUrl"] ?? "http://localhost:5080"));
            app.MapA2AHttpJson(agent, Path).RequireAuthorization();
            app.MapA2AJsonRpc(agent, Path).RequireAuthorization();
            return app;
        }

        public static AgentCard Card(string baseUrl) => new()
        {
            Name = AgentName,
            Description = "Answers questions about Contoso's expense policy.",
            Version = "1.0.0",
            SupportedInterfaces =
            [
                new AgentInterface { Url = baseUrl + Path, ProtocolBinding = ProtocolBindingNames.JsonRpc },
                new AgentInterface { Url = baseUrl + Path, ProtocolBinding = ProtocolBindingNames.HttpJson },
            ],
            Capabilities = new AgentCapabilities { Streaming = false, PushNotifications = false },
            DefaultInputModes = ["text/plain"],
            DefaultOutputModes = ["text/plain"],
            Skills = [new AgentSkill { Id = "expense-policy", Name = "Expense policy", Description = "Policy questions" }],
            SecuritySchemes = new() { ["bearer"] = new SecurityScheme { HttpAuthSecurityScheme = new() { Scheme = "Bearer", BearerFormat = "JWT" } } },
            SecurityRequirements = [new SecurityRequirement { Schemes = new() { ["bearer"] = new StringList { List = [] } } }],
        };
    }
}
