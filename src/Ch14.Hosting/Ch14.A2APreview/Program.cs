// ============================================================================
// Chapter 14 — A2A Protocol Integration
// Exposing an agent via the Agent-to-Agent (A2A) protocol using
// Microsoft Agent Framework's official A2A hosting library.
//
// The A2A protocol standardizes how agents discover and communicate
// with each other over HTTP. This sample demonstrates:
//   - Agent registration via AddAIAgent()
//   - A2A endpoint mapping via MapA2A()
//   - Agent Card discovery (automatic via the SDK)
//   - Sending tasks to the agent over A2A
//
// See: https://a2a-protocol.org/latest/
// Docs: https://learn.microsoft.com/en-us/agent-framework/integrations/a2a
// ============================================================================

using A2A.AspNetCore;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Extensions.AI;
using AgenticAI.Common.Extensions;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);

// Register an agent using the hosting library's AddAIAgent helper.
// This sets up the agent in DI with keyed services and session management.
// AddA2AServer registers an A2AServer for the agent in DI; the MapA2A* endpoint
// extensions below resolve it by agent name. AddA2AServer is required — the
// MapA2AHttpJson / MapA2AJsonRpc calls throw at runtime if it's missing.
var codingAssistant = builder.AddAIAgent(
    name: "coding-assistant",
    instructions: """
        You are a helpful coding assistant. Answer questions about .NET, C#,
        and software development. Be concise and accurate.
        """)
    .WithInMemorySessionStore()
    .AddA2AServer();

WebApplication app = builder.Build();

// Expose the agent via the A2A protocol.
// MAF 1.3 split MapA2A into two transport-specific extensions:
//   MapA2AHttpJson — REST-style POST /message and POST /message:stream
//   MapA2AJsonRpc  — JSON-RPC at the same path
// Agent card metadata is now configured at service registration via
// AddA2AServer, not as a parameter on the endpoint mapper.
app.MapA2AHttpJson(codingAssistant, "/a2a/coding-assistant");
app.MapA2AJsonRpc(codingAssistant, "/a2a/coding-assistant");

app.MapGet("/", () => Results.Ok(new
{
    service = "Chapter 14 — A2A Protocol Integration",
    endpoints = new
    {
        agentCard = "GET /a2a/coding-assistant/v1/card",
        message = "POST /a2a/coding-assistant/v1/message",
        stream = "POST /a2a/coding-assistant/v1/message:stream"
    }
}));

app.Run();
