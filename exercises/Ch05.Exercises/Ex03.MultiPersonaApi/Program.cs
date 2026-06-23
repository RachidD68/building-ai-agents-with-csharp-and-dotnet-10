// ============================================================================
// Exercise 03 — Multi-Persona API (Challenge)
// Minimal API with three endpoints, each backed by a different persona agent.
// FULLY STANDALONE.
// ============================================================================

using System.ClientModel;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Register IChatClient as singleton
builder.Services.AddSingleton<IChatClient>(sp =>
{
    IConfiguration config = sp.GetRequiredService<IConfiguration>();
    return CreateChatClient(config);
});

// Register persona agents as keyed singletons
builder.Services.AddKeyedSingleton("chef", (sp, _) =>
    new ChatClientAgent(
        sp.GetRequiredService<IChatClient>(),
        instructions: "You are Chef Gordon, a passionate celebrity chef. Relate everything to cooking. Be concise (2-3 sentences).",
        name: "ChefGordon",
        description: "A chef persona agent"));

builder.Services.AddKeyedSingleton("detective", (sp, _) =>
    new ChatClientAgent(
        sp.GetRequiredService<IChatClient>(),
        instructions: "You are Detective Holmes. Use deductive reasoning and Victorian speech. Be concise (2-3 sentences).",
        name: "DetectiveHolmes",
        description: "A detective persona agent"));

builder.Services.AddKeyedSingleton("poet", (sp, _) =>
    new ChatClientAgent(
        sp.GetRequiredService<IChatClient>(),
        instructions: "You are Poet Luna, a dreamy philosopher. Speak poetically with occasional rhymes. Be concise (2-3 sentences).",
        name: "PoetLuna",
        description: "A poet persona agent"));

WebApplication app = builder.Build();

// ---------------------------------------------------------------
// Endpoints
// ---------------------------------------------------------------

app.MapPost("/chef", async (
    [FromKeyedServices("chef")] ChatClientAgent agent,
    ChatRequest request,
    CancellationToken ct) =>
{
    AgentSession session = await agent.CreateSessionAsync(ct);
    AgentResponse response = await agent.RunAsync(request.Message, session, cancellationToken: ct);
    return Results.Ok(new ChatReply(agent.Name ?? "Chef", response.Text ?? string.Empty));
})
.WithName("AskChef")
.WithDescription("Ask Chef Gordon a question.");

app.MapPost("/detective", async (
    [FromKeyedServices("detective")] ChatClientAgent agent,
    ChatRequest request,
    CancellationToken ct) =>
{
    AgentSession session = await agent.CreateSessionAsync(ct);
    AgentResponse response = await agent.RunAsync(request.Message, session, cancellationToken: ct);
    return Results.Ok(new ChatReply(agent.Name ?? "Detective", response.Text ?? string.Empty));
})
.WithName("AskDetective")
.WithDescription("Ask Detective Holmes a question.");

app.MapPost("/poet", async (
    [FromKeyedServices("poet")] ChatClientAgent agent,
    ChatRequest request,
    CancellationToken ct) =>
{
    AgentSession session = await agent.CreateSessionAsync(ct);
    AgentResponse response = await agent.RunAsync(request.Message, session, cancellationToken: ct);
    return Results.Ok(new ChatReply(agent.Name ?? "Poet", response.Text ?? string.Empty));
})
.WithName("AskPoet")
.WithDescription("Ask Poet Luna a question.");

app.MapGet("/personas", () => Results.Ok(new[]
{
    new { Name = "ChefGordon", Endpoint = "/chef" },
    new { Name = "DetectiveHolmes", Endpoint = "/detective" },
    new { Name = "PoetLuna", Endpoint = "/poet" }
}))
.WithName("ListPersonas")
.WithDescription("Lists all available personas.");

Console.WriteLine("=== Exercise: Multi-Persona API ===");
Console.WriteLine("Endpoints: POST /chef, POST /detective, POST /poet, GET /personas");

app.Run();

// ---------------------------------------------------------------
// Provider factory
// ---------------------------------------------------------------

/// <summary>
/// Creates an <see cref="IChatClient"/> from configuration.
/// </summary>
static IChatClient CreateChatClient(IConfiguration configuration)
{
    string provider = configuration["LlmProvider:Provider"]?.ToLowerInvariant() ?? "github";

    return provider switch
    {
        "azure" => new AzureOpenAIClient(
                new Uri(configuration["LlmProvider:AzureOpenAI:Endpoint"] ?? throw new InvalidOperationException("Missing endpoint")),
                new DefaultAzureCredential())
            .GetChatClient(configuration["LlmProvider:AzureOpenAI:Model"] ?? "gpt-4o-mini")
            .AsIChatClient(),

        "openai" => new OpenAI.OpenAIClient(
                configuration["LlmProvider:OpenAI:ApiKey"] ?? throw new InvalidOperationException("Missing key"))
            .GetChatClient(configuration["LlmProvider:OpenAI:Model"] ?? "gpt-4o-mini")
            .AsIChatClient(),

        "github" => new OpenAI.OpenAIClient(
                new ApiKeyCredential(configuration["LlmProvider:GitHub:Token"] ?? throw new InvalidOperationException("Missing token")),
                new OpenAI.OpenAIClientOptions { Endpoint = new Uri(configuration["LlmProvider:GitHub:Endpoint"] ?? "https://models.github.ai/inference") })
            .GetChatClient(configuration["LlmProvider:GitHub:Model"] ?? "openai/gpt-4o-mini")
            .AsIChatClient(),

        "ollama" => new OllamaSharp.OllamaApiClient(
            new Uri(configuration["LlmProvider:Ollama:Endpoint"] ?? "http://localhost:11434"),
            configuration["LlmProvider:Ollama:Model"] ?? "llama3.2"),

        _ => throw new InvalidOperationException($"Unknown provider: '{provider}'")
    };
}

// ---------------------------------------------------------------
// Request/Response records
// ---------------------------------------------------------------

/// <summary>
/// Incoming chat request.
/// </summary>
/// <param name="Message">The user message.</param>
record ChatRequest(string Message);

/// <summary>
/// Outgoing chat reply.
/// </summary>
/// <param name="Persona">The persona that replied.</param>
/// <param name="Reply">The response text.</param>
record ChatReply(string Persona, string Reply);
