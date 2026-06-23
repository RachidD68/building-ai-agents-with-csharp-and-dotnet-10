// ============================================================================
// Chapter 5 — Hello, Agent! (Minimal API)
// Expose the same agent as an HTTP service using ASP.NET Core Minimal APIs.
//
//   POST /chat          → non-streaming JSON response
//   GET  /chat/stream   → streaming chunked text (Server-Sent Events style)
//
// This demonstrates that the same ChatClientAgent works identically whether
// called from a console app or a web endpoint.
// ============================================================================

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AgenticAI.Common.Extensions;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Register the IChatClient using the multi-provider factory from Common
builder.Services.AddLlmChatClient(builder.Configuration);

// Register the agent as a singleton so all requests share the same agent instance.
// Each request will create its own session (Chapter 6 covers persistent sessions).
//
// FORWARD REFERENCE: the Ch05 Challenge exercise (Ex03.MultiPersonaApi) extends
// this to several personas using keyed DI — AddKeyedSingleton(...) plus
// [FromKeyedServices("pirate")] on the endpoint — so one API can route to a
// different agent per request.
builder.Services.AddSingleton(sp =>
{
    IChatClient chatClient = sp.GetRequiredService<IChatClient>();
    return new ChatClientAgent(
        chatClient,
        instructions: """
            You are Captain CodeBeard, a legendary pirate who sailed the Seven Seas of Software.
            You speak in pirate dialect, love puns about programming, and always end your
            responses with a pirate-themed programming tip.
            Keep your answers concise — three to five sentences at most.
            """,
        name: "CaptainCodeBeard",
        description: "A pirate-themed AI assistant for programming questions");
});

WebApplication app = builder.Build();

// ---------------------------------------------------------------
// POST /chat — Non-streaming JSON response
// ---------------------------------------------------------------
app.MapPost("/chat", async (
    ChatRequest request,
    ChatClientAgent agent,
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

    return Results.Ok(new ChatResponseDto(
        Agent: agent.Name ?? "Agent",
        Message: response.Text ?? string.Empty,
        InputTokens: (int?)response.Usage?.InputTokenCount,
        OutputTokens: (int?)response.Usage?.OutputTokenCount));
});

// ---------------------------------------------------------------
// GET /chat/stream?message=... — Streaming chunked text
// ---------------------------------------------------------------
app.MapGet("/chat/stream", async (
    string? message,
    ChatClientAgent agent,
    HttpContext httpContext,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(message))
    {
        httpContext.Response.StatusCode = 400;
        await httpContext.Response.WriteAsync("Query parameter 'message' is required.",
            cancellationToken);
        return;
    }

    // Set headers for chunked streaming
    httpContext.Response.ContentType = "text/event-stream";
    httpContext.Response.Headers.CacheControl = "no-cache";
    httpContext.Response.Headers.Connection = "keep-alive";

    AgentSession session = await agent.CreateSessionAsync(cancellationToken);

    await foreach (AgentResponseUpdate update in agent.RunStreamingAsync(
        message,
        session,
        cancellationToken: cancellationToken))
    {
        if (update.Text is { Length: > 0 } text)
        {
            // Server-Sent Events format: each chunk on its own "data:" line.
            // Write AND flush through Response.BodyWriter (the PipeWriter API)
            // so the whole streaming path stays on the PipeWriter — the
            // high-performance route — instead of mixing it with the
            // Response.Body stream wrapper. Flushing via BodyWriter also lets
            // any active response filters (compression, chunked encoding) flush
            // alongside the data; Response.Body.FlushAsync writes through to the
            // underlying transport and can bypass those filters.
            await httpContext.Response.BodyWriter.WriteAsync(
                System.Text.Encoding.UTF8.GetBytes($"data: {text}\n\n"),
                cancellationToken);
            await httpContext.Response.BodyWriter.FlushAsync(cancellationToken);
        }
    }

    // Signal end of stream — same PipeWriter (BodyWriter) path as the chunks.
    await httpContext.Response.BodyWriter.WriteAsync(
        System.Text.Encoding.UTF8.GetBytes("data: [DONE]\n\n"), cancellationToken);
    await httpContext.Response.BodyWriter.FlushAsync(cancellationToken);
});

// ---------------------------------------------------------------
// GET / — Health check and usage instructions
// ---------------------------------------------------------------
app.MapGet("/", () => Results.Ok(new
{
    service = "Captain CodeBeard Agent API",
    endpoints = new
    {
        chat = "POST /chat with JSON body { \"message\": \"your question\" }",
        stream = "GET /chat/stream?message=your+question"
    }
}));

app.Run();

// ---------------------------------------------------------------
// Request/Response DTOs
// ---------------------------------------------------------------

/// <summary>
/// Incoming chat request body.
/// </summary>
/// <param name="Message">The user's message to send to the agent.</param>
internal sealed record ChatRequest(string Message);

/// <summary>
/// Chat response returned from the non-streaming endpoint.
/// </summary>
/// <param name="Agent">The name of the agent that responded.</param>
/// <param name="Message">The agent's response text.</param>
/// <param name="InputTokens">Number of input tokens consumed, if available.</param>
/// <param name="OutputTokens">Number of output tokens generated, if available.</param>
internal sealed record ChatResponseDto(
    string Agent,
    string Message,
    int? InputTokens,
    int? OutputTokens);
