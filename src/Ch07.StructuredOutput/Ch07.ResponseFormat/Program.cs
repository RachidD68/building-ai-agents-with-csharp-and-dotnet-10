// ============================================================================
// Chapter 7 — Structured Output (ResponseFormat)
// The "other door" to structured output. Where RunAsync<T>() deserializes for
// you, ResponseFormat sets the schema on the run options (or the agent) and
// hands back JSON text you parse yourself. Use it when the type is dynamic,
// the schema is raw JSON, the format must be fixed at agent-creation time, you
// only want the raw text, or you are wiring agents together (Chapters 11-12).
//
// Paths demonstrated:
//   1. ChatResponseFormat.Text / ChatResponseFormat.Json — plain text vs. schemaless JSON
//   2. ChatResponseFormat.ForJsonSchema<T>()             — typed schema on AgentRunOptions
//   3. ChatResponseFormat.ForJsonSchema(JsonElement,...)  — raw JSON-schema string
//   4. MovieListWrapper                                   — the primitives/arrays workaround
//   5. RunStreamingAsync + ToAgentResponseAsync()         — assemble-then-parse with streaming
// ============================================================================

using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgenticAI.Common.Extensions;
using Ch07.ResponseFormat;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);

using IHost host = builder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();

Console.WriteLine("=== Chapter 7: Structured Output — ResponseFormat ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// The same options drive schema generation and our own deserialization.
// JsonSerializerOptions.Web gives us camelCase + case-insensitive matching,
// which lines up with the [JsonPropertyName] attributes on the records.
JsonSerializerOptions jsonOptions = new(JsonSerializerOptions.Web)
{
    WriteIndented = true
};

ChatClientAgent agent = new(
    chatClient,
    instructions: """
        You are a structured data assistant.
        When asked for JSON, respond with valid JSON only — no prose, no code fences.
        Be accurate and fill in every field.
        """,
    name: "ResponseFormatBot");

try
{
    // -----------------------------------------------------------------
    // Path 1: ChatResponseFormat.Text and ChatResponseFormat.Json
    // The two built-ins. Text is the default (prose). Json asks for a JSON
    // object with no particular schema — useful when you just want "some JSON."
    // -----------------------------------------------------------------
    Console.WriteLine("--- Path 1: ChatResponseFormat.Json (schemaless) ---\n");

    AgentSession jsonSession = await agent.CreateSessionAsync(cts.Token);

    ChatClientAgentRunOptions jsonRun = new(new ChatOptions
    {
        ResponseFormat = ChatResponseFormat.Json
    });

    AgentResponse schemalessResponse = await agent.RunAsync(
        "List three primary colors as a JSON object with a 'colors' array.",
        jsonSession,
        jsonRun,
        cancellationToken: cts.Token);

    Console.WriteLine(schemalessResponse.Text);
    Console.WriteLine();

    // -----------------------------------------------------------------
    // Path 2: ChatResponseFormat.ForJsonSchema<T>() on the run options
    // The typed-schema door. MAF derives the schema from BookSummary and sends
    // it with the request; the model is constrained to that shape. We get JSON
    // text back and deserialize it ourselves with the same options.
    // -----------------------------------------------------------------
    Console.WriteLine("--- Path 2: ForJsonSchema<BookSummary>() on AgentRunOptions ---\n");

    AgentSession bookSession = await agent.CreateSessionAsync(cts.Token);

    ChatClientAgentRunOptions bookRun = new(new ChatOptions
    {
        ResponseFormat = ChatResponseFormat.ForJsonSchema<BookSummary>(jsonOptions)
    });

    AgentResponse bookResponse = await agent.RunAsync(
        "Summarize the novel '1984' by George Orwell.",
        bookSession,
        bookRun,
        cancellationToken: cts.Token);

    BookSummary book = JsonSerializer.Deserialize<BookSummary>(bookResponse.Text, jsonOptions)!;

    Console.WriteLine($"Title     : {book.Title}");
    Console.WriteLine($"Author    : {book.Author}");
    Console.WriteLine($"Published : {book.YearPublished}");
    Console.WriteLine($"Summary   : {book.Summary}");
    Console.WriteLine($"Themes    : {string.Join(", ", book.Themes)}");
    Console.WriteLine();

    // -----------------------------------------------------------------
    // Path 3: ChatResponseFormat.ForJsonSchema(JsonElement, name, description)
    // The raw-JSON-schema door. No .NET type at all — the schema is a string,
    // exactly what you would load from external config or a declarative agent
    // definition. We read the result with JsonElement.GetProperty(...).
    // -----------------------------------------------------------------
    Console.WriteLine("--- Path 3: raw JSON-schema string ---\n");

    const string rawSchema = """
        {
          "type": "object",
          "properties": {
            "city": { "type": "string" },
            "country": { "type": "string" },
            "populationMillions": { "type": "number" }
          },
          "required": ["city", "country", "populationMillions"]
        }
        """;

    AgentSession citySession = await agent.CreateSessionAsync(cts.Token);

    ChatClientAgentRunOptions cityRun = new(new ChatOptions
    {
        ResponseFormat = ChatResponseFormat.ForJsonSchema(
            JsonDocument.Parse(rawSchema).RootElement,
            schemaName: "CityFact",
            schemaDescription: "A single fact about a city.")
    });

    AgentResponse cityResponse = await agent.RunAsync(
        "Give one fact about Tokyo, Japan.",
        citySession,
        cityRun,
        cancellationToken: cts.Token);

    JsonElement city = JsonSerializer.Deserialize<JsonElement>(cityResponse.Text);
    Console.WriteLine($"City       : {city.GetProperty("city").GetString()}");
    Console.WriteLine($"Country    : {city.GetProperty("country").GetString()}");
    Console.WriteLine($"Population : {city.GetProperty("populationMillions").GetDouble()}M");
    Console.WriteLine();

    // -----------------------------------------------------------------
    // Path 4: the primitives/arrays workaround
    // ResponseFormat needs a top-level JSON object, so a bare List<string>
    // is not allowed. Wrap it in a type. (RunAsync<T>() would accept the list
    // directly — this limitation is specific to the ResponseFormat path.)
    // -----------------------------------------------------------------
    Console.WriteLine("--- Path 4: MovieListWrapper (array workaround) ---\n");

    AgentSession movieSession = await agent.CreateSessionAsync(cts.Token);

    ChatClientAgentRunOptions movieRun = new(new ChatOptions
    {
        ResponseFormat = ChatResponseFormat.ForJsonSchema<MovieListWrapper>(jsonOptions)
    });

    AgentResponse movieResponse = await agent.RunAsync(
        "Recommend three classic science-fiction films.",
        movieSession,
        movieRun,
        cancellationToken: cts.Token);

    MovieListWrapper movies = JsonSerializer.Deserialize<MovieListWrapper>(movieResponse.Text, jsonOptions)!;
    foreach (string movie in movies.Movies)
    {
        Console.WriteLine($"  - {movie}");
    }
    Console.WriteLine();

    // -----------------------------------------------------------------
    // Path 5: typed output while streaming
    // You cannot deserialize mid-stream — there is no partial typed object.
    // Stream the updates for a responsive UI, assemble them into one response
    // with ToAgentResponseAsync(), then deserialize the assembled text.
    // -----------------------------------------------------------------
    Console.WriteLine("--- Path 5: streaming + ToAgentResponseAsync() ---\n");

    AgentSession streamSession = await agent.CreateSessionAsync(cts.Token);

    ChatClientAgentRunOptions streamRun = new(new ChatOptions
    {
        ResponseFormat = ChatResponseFormat.ForJsonSchema<BookSummary>(jsonOptions)
    });

    IAsyncEnumerable<AgentResponseUpdate> updates = agent.RunStreamingAsync(
        "Summarize the novel 'Brave New World' by Aldous Huxley.",
        streamSession,
        streamRun,
        cancellationToken: cts.Token);

    // Assemble first, parse second.
    AgentResponse assembled = await updates.ToAgentResponseAsync(cts.Token);
    BookSummary streamed = JsonSerializer.Deserialize<BookSummary>(assembled.Text, jsonOptions)!;

    Console.WriteLine($"Title     : {streamed.Title}");
    Console.WriteLine($"Author    : {streamed.Author}");
    Console.WriteLine($"Published : {streamed.YearPublished}");
    Console.WriteLine($"Themes    : {string.Join(", ", streamed.Themes)}");
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n[Cancelled by user]");
}
catch (JsonException ex)
{
    Console.WriteLine($"\nJSON deserialization error: {ex.Message}");
    Console.WriteLine("The model returned text that did not match the requested schema.");
    Console.WriteLine("Smaller local models are more prone to this; try a larger model.");
}

Console.WriteLine("\n=== ResponseFormat demo complete! ===");
