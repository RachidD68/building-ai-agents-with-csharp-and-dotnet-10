// ============================================================================
// Exercise 03 — Multi-Persona API (Challenge)
// Extends Ch05.MinimalApiAgent: the ONE /chat endpoint now accepts a persona
// field in the request body ("pirate", "navigator", "helper") and resolves a
// different ChatClientAgent per persona via keyed DI. GET /personas returns
// the available personas WITH their descriptions. FULLY STANDALONE.
//
// Keyed DI: AddKeyedSingleton<ChatClientAgent>("pirate", (sp, _) => ...)
// registers each agent. [FromKeyedServices("pirate")] binds ONE fixed key at
// compile time — the persona here comes from the request body, so the endpoint
// resolves dynamically with sp.GetRequiredKeyedService<ChatClientAgent>(key).
// ============================================================================

using System.ClientModel;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// The persona catalog: DI key, agent name, description (surfaced by
// GET /personas), and the instructions behind each agent. The three personas
// are the chapter's three example agents.
(string Key, string Name, string Description, string Instructions)[] personaCatalog =
[
    ("pirate",
     "CaptainCodeBeard",
     "A pirate-themed AI assistant for programming questions",
     """
     You are Captain CodeBeard, a legendary pirate who sailed the Seven Seas of Software.
     You speak in pirate dialect, love puns about programming, and always end your
     responses with a pirate-themed programming tip.
     Keep your answers concise — three to five sentences at most.
     """),

    ("navigator",
     "NavigatorNova",
     "A space-explorer AI that explains concepts using space metaphors",
     """
     You are NavigatorNova, a space-explorer AI charting the galaxy of software.
     Explain every concept using space and navigation metaphors.
     Keep your answers concise — three to five sentences at most.
     """),

    ("helper",
     "QuickHelper",
     "A no-frills assistant that answers plainly and concisely",
     "You are a helpful assistant. Be concise.")
];

// Register IChatClient as singleton — shared by all three agents.
builder.Services.AddSingleton<IChatClient>(sp =>
{
    IConfiguration config = sp.GetRequiredService<IConfiguration>();
    return CreateChatClient(config);
});

// Register one keyed ChatClientAgent per persona.
foreach ((string key, string name, string description, string instructions) in personaCatalog)
{
    builder.Services.AddKeyedSingleton(key, (sp, _) =>
        new ChatClientAgent(
            sp.GetRequiredService<IChatClient>(),
            instructions: instructions,
            name: name,
            description: description));
}

WebApplication app = builder.Build();

// ---------------------------------------------------------------
// POST /chat — one endpoint, persona selected by the request body
// ---------------------------------------------------------------
app.MapPost("/chat", async (
    ChatRequest request,
    IServiceProvider services,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Message))
    {
        return Results.BadRequest(new { error = "Message is required." });
    }

    string personaKey = request.Persona?.Trim().ToLowerInvariant() ?? string.Empty;
    if (!personaCatalog.Any(p => p.Key == personaKey))
    {
        return Results.BadRequest(new
        {
            error = $"Unknown persona '{request.Persona}'. See GET /personas for the available personas."
        });
    }

    // Dynamic keyed resolution — the runtime equivalent of
    // [FromKeyedServices("<persona>")] for a key only known per request.
    ChatClientAgent agent = services.GetRequiredKeyedService<ChatClientAgent>(personaKey);

    AgentSession session = await agent.CreateSessionAsync(cancellationToken);

    AgentResponse response = await agent.RunAsync(
        request.Message,
        session,
        cancellationToken: cancellationToken);

    return Results.Ok(new ChatResponseDto(
        Persona: personaKey,
        Agent: agent.Name ?? personaKey,
        Message: response.Text ?? string.Empty,
        InputTokens: (int?)response.Usage?.InputTokenCount,
        OutputTokens: (int?)response.Usage?.OutputTokenCount));
})
.WithName("Chat")
.WithDescription("Chat with the persona named in the request body (pirate, navigator, helper).");

// ---------------------------------------------------------------
// GET /personas — the available personas and their descriptions
// ---------------------------------------------------------------
app.MapGet("/personas", () => Results.Ok(
    personaCatalog.Select(p => new PersonaDto(p.Key, p.Name, p.Description))))
.WithName("ListPersonas")
.WithDescription("Lists the available personas with their descriptions.");

// ---------------------------------------------------------------
// GET / — usage instructions
// ---------------------------------------------------------------
app.MapGet("/", () => Results.Ok(new
{
    service = "Multi-Persona Agent API",
    endpoints = new
    {
        chat = "POST /chat with JSON body { \"persona\": \"pirate|navigator|helper\", \"message\": \"your question\" }",
        personas = "GET /personas"
    }
}));

Console.WriteLine("=== Exercise: Multi-Persona API ===");
Console.WriteLine("Endpoints: POST /chat (persona field in body), GET /personas");

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
/// <param name="Persona">The persona to answer as: "pirate", "navigator", or "helper".</param>
/// <param name="Message">The user message.</param>
record ChatRequest(string Persona, string Message);

/// <summary>
/// Outgoing chat reply.
/// </summary>
/// <param name="Persona">The persona key that handled the request.</param>
/// <param name="Agent">The name of the agent that responded.</param>
/// <param name="Message">The agent's response text.</param>
/// <param name="InputTokens">Number of input tokens consumed, if available.</param>
/// <param name="OutputTokens">Number of output tokens generated, if available.</param>
record ChatResponseDto(
    string Persona,
    string Agent,
    string Message,
    int? InputTokens,
    int? OutputTokens);

/// <summary>
/// A persona listing entry.
/// </summary>
/// <param name="Persona">The persona key to send in the /chat request body.</param>
/// <param name="Name">The agent's name.</param>
/// <param name="Description">What this persona is for.</param>
record PersonaDto(string Persona, string Name, string Description);
