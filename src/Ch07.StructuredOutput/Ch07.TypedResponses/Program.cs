// ============================================================================
// Chapter 7 — Structured Output and Typed Responses
// Demonstrates RunAsync<T>() to get strongly-typed C# objects from the LLM
// instead of raw text. The agent returns JSON that maps directly to records.
//
// Key concepts:
//   - RunAsync<T>():             returns AgentResponse<T> with deserialized Result
//   - ChatResponseFormatJson:    instructs the model to return valid JSON
//   - JSON schema generation:    automatic schema from C# types
//   - Type-safe agent responses: work with C# objects, not strings
// ============================================================================

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgenticAI.Common.Extensions;
using Ch07.TypedResponses;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);

using IHost host = builder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();

Console.WriteLine("=== Chapter 7: Structured Output — Typed Responses ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// JSON serializer options for pretty-printing results
JsonSerializerOptions jsonOptions = new()
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
};

// Create the agent with instructions for structured output
ChatClientAgent agent = new(
    chatClient,
    instructions: """
        You are a structured data extraction assistant.
        Always respond with valid JSON matching the requested schema.
        Do not include any text outside the JSON object.
        Be accurate, specific, and fill in all fields.
        """,
    name: "StructuredBot");

try
{
    // ---------------------------------------------------------------
    // Example 1: Weather Report
    // ---------------------------------------------------------------
    Console.WriteLine("--- Example 1: Weather Report ---\n");

    AgentSession weatherSession = await agent.CreateSessionAsync(cts.Token);

    AgentResponse<WeatherReport> weatherResponse = await agent.RunAsync<WeatherReport>(
        "Generate a realistic weather report for Seattle, Washington in March.",
        weatherSession,jsonOptions,cancellationToken: cts.Token);

    WeatherReport weather = weatherResponse.Result;

    Console.WriteLine($"Location     : {weather.Location}");
    Console.WriteLine($"Temperature  : {weather.TemperatureCelsius}°C");
    Console.WriteLine($"Conditions   : {weather.Conditions}");
    Console.WriteLine($"Humidity     : {weather.HumidityPercent}%");
    Console.WriteLine($"Wind Speed   : {weather.WindSpeedKmh} km/h");
    Console.WriteLine($"Summary      : {weather.Summary}");
    Console.WriteLine();

    // ---------------------------------------------------------------
    // Example 2: Movie Review
    // ---------------------------------------------------------------
    Console.WriteLine("--- Example 2: Movie Review ---\n");

    AgentSession movieSession = await agent.CreateSessionAsync(cts.Token);

    AgentResponse<MovieReview> movieResponse = await agent.RunAsync<MovieReview>(
        "Write a review of the movie 'Inception' (2010) by Christopher Nolan.",
        movieSession, jsonOptions,  cancellationToken: cts.Token);

    MovieReview review = movieResponse.Result;

    Console.WriteLine($"Title          : {review.Title}");
    Console.WriteLine($"Rating         : {review.Rating}/10");
    Console.WriteLine($"Genre          : {review.Genre}");
    Console.WriteLine($"Strengths      : {string.Join(", ", review.Strengths)}");
    Console.WriteLine($"Weaknesses     : {string.Join(", ", review.Weaknesses)}");
    Console.WriteLine($"Recommendation : {review.Recommendation}");
    Console.WriteLine();

    // ---------------------------------------------------------------
    // Example 3: Task List
    // ---------------------------------------------------------------
    Console.WriteLine("--- Example 3: Task List from Natural Language ---\n");

    string taskDescription = """
        I need to build a REST API for a bookstore. The main tasks are:
        setting up the project with ASP.NET Core, designing the database
        schema for books and authors, implementing CRUD endpoints,
        adding authentication with JWT, writing integration tests,
        and deploying to Azure App Service.
        """;

    Console.WriteLine($"Input: {taskDescription.Trim()}\n");

    AgentSession taskSession = await agent.CreateSessionAsync(cts.Token);

    AgentResponse<TaskList> taskResponse = await agent.RunAsync<TaskList>(
        $"Break this into a prioritized task list: {taskDescription}",
        taskSession,jsonOptions, cancellationToken: cts.Token);

    TaskList taskList = taskResponse.Result;

    Console.WriteLine($"Project: {taskList.ProjectName}\n");
    foreach (TaskItem task in taskList.Tasks)
    {
        Console.WriteLine($"  [{task.Priority,-6}] {task.Title}");
        Console.WriteLine($"          {task.Description}");
        Console.WriteLine($"          Estimated: {task.EstimatedTime}");
    }
    Console.WriteLine();

    // ---------------------------------------------------------------
    // Show the raw JSON for inspection
    // ---------------------------------------------------------------
    Console.WriteLine("--- Raw JSON (for inspection) ---\n");
    string rawJson = JsonSerializer.Serialize(taskList, jsonOptions);
    Console.WriteLine(rawJson);
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n[Cancelled by user]");
}
catch (JsonException ex)
{
    Console.WriteLine($"\nJSON deserialization error: {ex.Message}");
    Console.WriteLine("The LLM response did not match the expected schema.");
    Console.WriteLine("This can happen with less capable models. Try a different model or");
    Console.WriteLine("add more explicit schema instructions to the system prompt.");
}

Console.WriteLine("\n=== Typed Responses demo complete! ===");
