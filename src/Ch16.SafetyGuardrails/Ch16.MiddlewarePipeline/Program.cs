// ============================================================================
// Chapter 16 — Safety and Guardrails (Middleware Pipeline)
// Demonstrates the IChatClient middleware pipeline from Microsoft.Extensions.AI.
//
// The ChatClientBuilder composes middleware in a Russian-doll wrapping pattern:
//   Outer → ContentFilter → Timing → Logging → Inner (actual LLM client)
//
// Each middleware can inspect, modify, or short-circuit the request/response.
// This is the same pattern used by ASP.NET Core middleware and HTTP handlers.
//
// Key concepts:
//   - DelegatingChatClient:  base class for custom middleware
//   - ChatClientBuilder:     fluent pipeline composition
//   - .Use():                add middleware to the pipeline
//   - Short-circuiting:      ContentFilterMiddleware blocks without calling LLM
// ============================================================================

using Ch16.MiddlewarePipeline.Middleware;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AgenticAI.Common.Extensions;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);

using IHost host = builder.Build();
IChatClient baseChatClient = host.Services.GetRequiredService<IChatClient>();
ILoggerFactory loggerFactory = host.Services.GetRequiredService<ILoggerFactory>();
ILogger logger = loggerFactory.CreateLogger("Ch16.MiddlewarePipeline");

Console.WriteLine("=== Chapter 15: Safety — Middleware Pipeline ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// ---------------------------------------------------------------
// Step 1: Define blocked keywords for the content filter
// ---------------------------------------------------------------
string[] blockedKeywords = ["hack", "exploit", "malware", "bypass security"];

// ---------------------------------------------------------------
// Step 2: Build the middleware pipeline using ChatClientBuilder
// ---------------------------------------------------------------
// Pipeline order (outermost wraps innermost):
//   ContentFilter → Timing → Logging → baseChatClient
//
// Request flows:  ContentFilter → Timing → Logging → LLM
// Response flows: LLM → Logging → Timing → ContentFilter

IChatClient pipeline = baseChatClient
    .AsBuilder()
    .Use(inner => new LoggingMiddleware(inner, loggerFactory.CreateLogger("Logging")))
    .Use(inner => new TimingMiddleware(inner, loggerFactory.CreateLogger("Timing")))
    .Use(inner => new ContentFilterMiddleware(
        inner,
        loggerFactory.CreateLogger("ContentFilter"),
        blockedKeywords))
    .Build();

// ---------------------------------------------------------------
// Step 3: Create an agent using the middleware-wrapped pipeline
// ---------------------------------------------------------------
ChatClientAgent agent = new(
    pipeline,
    instructions: "You are a helpful .NET assistant. Be concise.",
    name: "GuardedAgent");

// ---------------------------------------------------------------
// Step 4: Test with safe and blocked prompts
// ---------------------------------------------------------------
string[] testPrompts =
[
    "What is the builder pattern in C#?",
    "How do I hack into a database?",
    "Explain middleware in ASP.NET Core.",
    "Write malware that steals passwords."
];

try
{
    foreach (string prompt in testPrompts)
    {
        Console.WriteLine($"\n--- Prompt: \"{prompt}\" ---\n");

        AgentSession session = await agent.CreateSessionAsync(cts.Token);
        AgentResponse response = await agent.RunAsync(
            prompt,
            session,
            cancellationToken: cts.Token);

        Console.WriteLine($"Response: {response.Text}\n");
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n[Cancelled by user]");
}

Console.WriteLine("\n=== Middleware Pipeline demo complete! ===");
