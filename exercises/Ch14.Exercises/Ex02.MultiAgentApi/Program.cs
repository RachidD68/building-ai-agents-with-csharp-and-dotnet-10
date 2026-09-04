// ============================================================================
// Exercise 02 — Multi-Agent API (Intermediate)
// Web API hosting FOUR keyed agents — translator, summarizer, formalizer,
// classifier — each at its own endpoint via [FromKeyedServices], plus a
// /agents discovery endpoint and an SSE streaming endpoint for the
// summarizer. FULLY STANDALONE.
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

builder.Services.AddKeyedSingleton("formalizer", (sp, _) =>
    new ChatClientAgent(sp.GetRequiredService<IChatClient>(),
        instructions: "Rewrite the given text in formal business tone, preserving meaning. Return ONLY the rewritten text.",
        name: "Formalizer"));

builder.Services.AddKeyedSingleton("classifier", (sp, _) =>
    new ChatClientAgent(sp.GetRequiredService<IChatClient>(),
        instructions: "You are a text classifier. Classify the given text and return: sentiment (positive/negative/neutral), a one-or-two-word topic label, and a one-sentence justification.",
        name: "Classifier"));

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

app.MapPost("/formalize", async (
    [FromKeyedServices("formalizer")] ChatClientAgent agent,
    AgentRequest req, CancellationToken ct) =>
{
    AgentSession session = await agent.CreateSessionAsync(ct);
    AgentResponse response = await agent.RunAsync(req.Text, session, cancellationToken: ct);
    return Results.Ok(new AgentResult(agent.Name ?? "formalizer", response.Text ?? ""));
}).WithName("Formalize");

app.MapPost("/classify", async (
    [FromKeyedServices("classifier")] ChatClientAgent agent,
    AgentRequest req, CancellationToken ct) =>
{
    AgentSession session = await agent.CreateSessionAsync(ct);
    AgentResponse response = await agent.RunAsync(req.Text, session, cancellationToken: ct);
    return Results.Ok(new AgentResult(agent.Name ?? "classifier", response.Text ?? ""));
}).WithName("Classify");

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
    new { Name = "Formalizer", Endpoint = "/formalize" },
    new { Name = "Classifier", Endpoint = "/classify" }
})).WithName("ListAgents");

Console.WriteLine("=== Exercise: Multi-Agent API ===");
Console.WriteLine("Endpoints: POST /translate, /summarize, /summarize/stream (SSE), /formalize, /classify | GET /agents");
app.Run();

/// <summary>Creates an IChatClient from configuration.</summary>
static IChatClient CreateChatClient(IConfiguration config)
{
    string provider = config["LlmProvider:Provider"]?.ToLowerInvariant() ?? "ollama";
    return provider switch
    {
        "azure" => new AzureOpenAIClient(new Uri(config["LlmProvider:AzureOpenAI:Endpoint"] ?? throw new InvalidOperationException("Missing")), new DefaultAzureCredential())
            .GetChatClient(config["LlmProvider:AzureOpenAI:Model"] ?? "gpt-5.6-luna").AsIChatClient(),
        "openai" => new OpenAI.OpenAIClient(config["LlmProvider:OpenAI:ApiKey"] ?? throw new InvalidOperationException("Missing"))
            .GetChatClient(config["LlmProvider:OpenAI:Model"] ?? "gpt-5.6-luna").AsIChatClient(),
        "ollama" => new OllamaSharp.OllamaApiClient(new Uri(config["LlmProvider:Ollama:Endpoint"] ?? "http://localhost:11434"), config["LlmProvider:Ollama:Model"] ?? "llama3.2"),
        _ => throw new InvalidOperationException($"Unknown provider: '{provider}'")
    };
}

/// <summary>Request payload.</summary>
record AgentRequest(string Text);

/// <summary>Response payload.</summary>
record AgentResult(string Agent, string Result);
