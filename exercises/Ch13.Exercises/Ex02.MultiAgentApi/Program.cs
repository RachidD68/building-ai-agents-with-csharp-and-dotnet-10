// ============================================================================
// Exercise 02 — Multi-Agent API (Intermediate)
// Web API hosting 3 agents with keyed services. FULLY STANDALONE.
// ============================================================================

using System.ClientModel;
using System.Runtime.CompilerServices;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IChatClient>(sp =>
    CreateChatClient(sp.GetRequiredService<IConfiguration>()));

builder.Services.AddKeyedSingleton("translator", (sp, _) =>
    new ChatClientAgent(sp.GetRequiredService<IChatClient>(),
        instructions: "You are a translator. Translate the given text to English. Return ONLY the translation.",
        name: "Translator"));

builder.Services.AddKeyedSingleton("summarizer", (sp, _) =>
    new ChatClientAgent(sp.GetRequiredService<IChatClient>(),
        instructions: "You are a summarizer. Condense the given text into 2-3 sentences. Return ONLY the summary.",
        name: "Summarizer"));

builder.Services.AddKeyedSingleton("analyzer", (sp, _) =>
    new ChatClientAgent(sp.GetRequiredService<IChatClient>(),
        instructions: "You are a sentiment analyzer. Analyze the sentiment of the given text. Return: sentiment (positive/negative/neutral), confidence (high/medium/low), and a brief explanation.",
        name: "Analyzer"));

WebApplication app = builder.Build();

app.MapPost("/translate", async (
    [FromKeyedServices("translator")] ChatClientAgent agent,
    AgentRequest req, CancellationToken ct) =>
{
    AgentSession session = await agent.CreateSessionAsync(ct);
    AgentResponse response = await agent.RunAsync(req.Text, session, cancellationToken: ct);
    return Results.Ok(new AgentResult(agent.Name ?? "translator", response.Text ?? ""));
}).WithName("Translate");

app.MapPost("/summarize", async (
    [FromKeyedServices("summarizer")] ChatClientAgent agent,
    AgentRequest req, CancellationToken ct) =>
{
    AgentSession session = await agent.CreateSessionAsync(ct);
    AgentResponse response = await agent.RunAsync(req.Text, session, cancellationToken: ct);
    return Results.Ok(new AgentResult(agent.Name ?? "summarizer", response.Text ?? ""));
}).WithName("Summarize");

app.MapPost("/analyze", async (
    [FromKeyedServices("analyzer")] ChatClientAgent agent,
    AgentRequest req, CancellationToken ct) =>
{
    AgentSession session = await agent.CreateSessionAsync(ct);
    AgentResponse response = await agent.RunAsync(req.Text, session, cancellationToken: ct);
    return Results.Ok(new AgentResult(agent.Name ?? "analyzer", response.Text ?? ""));
}).WithName("Analyze");

// SSE: stream the summarizer's output token by token. TypedResults.ServerSentEvents
// writes each string as a `data:` frame under the "token" event type; the framework
// CancellationToken propagates, so a client disconnect cancels the in-flight call.
app.MapPost("/summarize/stream", (
    [FromKeyedServices("summarizer")] ChatClientAgent agent,
    AgentRequest req, CancellationToken cancellationToken) =>
{
    async IAsyncEnumerable<string> StreamTokensAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        AgentSession session = await agent.CreateSessionAsync(ct);

        await foreach (AgentResponseUpdate update in
            agent.RunStreamingAsync(req.Text, session, cancellationToken: ct))
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
}).WithName("SummarizeStream");

app.MapGet("/agents", () => Results.Ok(new[]
{
    new { Name = "Translator", Endpoint = "/translate" },
    new { Name = "Summarizer", Endpoint = "/summarize" },
    new { Name = "Summarizer", Endpoint = "/summarize/stream (SSE)" },
    new { Name = "Analyzer", Endpoint = "/analyze" }
})).WithName("ListAgents");

Console.WriteLine("=== Exercise: Multi-Agent API ===");
Console.WriteLine("Endpoints: POST /translate, /summarize, /summarize/stream (SSE), /analyze | GET /agents");
app.Run();

/// <summary>Creates an IChatClient from configuration.</summary>
static IChatClient CreateChatClient(IConfiguration config)
{
    string provider = config["LlmProvider:Provider"]?.ToLowerInvariant() ?? "github";
    return provider switch
    {
        "azure" => new AzureOpenAIClient(new Uri(config["LlmProvider:AzureOpenAI:Endpoint"] ?? throw new InvalidOperationException("Missing")), new DefaultAzureCredential())
            .GetChatClient(config["LlmProvider:AzureOpenAI:Model"] ?? "gpt-4o-mini").AsIChatClient(),
        "openai" => new OpenAI.OpenAIClient(config["LlmProvider:OpenAI:ApiKey"] ?? throw new InvalidOperationException("Missing"))
            .GetChatClient(config["LlmProvider:OpenAI:Model"] ?? "gpt-4o-mini").AsIChatClient(),
        "github" => new OpenAI.OpenAIClient(new ApiKeyCredential(config["LlmProvider:GitHub:Token"] ?? throw new InvalidOperationException("Missing")),
                new OpenAI.OpenAIClientOptions { Endpoint = new Uri(config["LlmProvider:GitHub:Endpoint"] ?? "https://models.github.ai/inference") })
            .GetChatClient(config["LlmProvider:GitHub:Model"] ?? "openai/gpt-4o-mini").AsIChatClient(),
        "ollama" => new OllamaSharp.OllamaApiClient(new Uri(config["LlmProvider:Ollama:Endpoint"] ?? "http://localhost:11434"), config["LlmProvider:Ollama:Model"] ?? "llama3.2"),
        _ => throw new InvalidOperationException($"Unknown provider: '{provider}'")
    };
}

/// <summary>Request payload.</summary>
record AgentRequest(string Text);

/// <summary>Response payload.</summary>
record AgentResult(string Agent, string Result);
