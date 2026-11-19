// ============================================================================
// Chapter 8 — Tools and Function Calling (Weather Tool)
// Demonstrates the full tool-calling loop: define a tool, register it with the
// agent, and let the LLM decide when to invoke it vs. answering directly.
//
// Key concepts:
//   - AIFunctionFactory.Create() to wrap a method as an AITool
//   - [Description] on methods and parameters for LLM understanding
//   - ChatClientAgentOptions.Tools for tool registration
//   - Graceful degradation when a tool call fails
//
// FORWARD REFERENCE: In Chapter 9, we'll see how tools can come from an
// external MCP server rather than being defined inline.
// ============================================================================

using System.ComponentModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgenticAI.Common.Extensions;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);

using IHost host = builder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();

Console.WriteLine("=== Chapter 8: Weather Tool — Function Calling Demo ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// ---------------------------------------------------------------
// Step 1: Define the tool and register it with the agent
// ---------------------------------------------------------------
// AIFunctionFactory.Create wraps a delegate as an AIFunction that the LLM
// can call. The [Description] attributes tell the LLM what the tool does.
AIFunction weatherTool = AIFunctionFactory.Create(GetWeatherAsync);

ChatClientAgent agent = new(
    chatClient,
    instructions: "You are a helpful weather assistant. Answer questions about the weather using the provided tool. For non-weather questions, answer directly from your knowledge.",
    name: "WeatherAssistant",
    description: "An assistant that can look up weather data for any city",
    tools: [weatherTool]);

// ---------------------------------------------------------------
// Step 2: Ask questions — some need the tool, some don't
// ---------------------------------------------------------------
string[] prompts =
[
    "What's the weather like in Seattle right now?",
    "Compare the weather in Tokyo and London.",
    "What is the capital of France?",       // No tool needed
    "Is it a good day for a picnic in Miami?"
];

foreach (string prompt in prompts)
{
    AgentSession session = await agent.CreateSessionAsync(cts.Token);
    Console.WriteLine($"You: {prompt}");

    try
    {
        AgentResponse response = await agent.RunAsync(
            prompt,
            session,
            cancellationToken: cts.Token);

        Console.WriteLine($"Agent: {response.Text}\n");
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("\n[Cancelled by user]");
        return;
    }
}

// ---------------------------------------------------------------
// Step 3: Demonstrate graceful degradation
// ---------------------------------------------------------------
Console.WriteLine("--- Graceful Degradation: Simulating API Failure ---\n");

WeatherToolState.SimulateFailure = true;

AgentSession failSession = await agent.CreateSessionAsync(cts.Token);

try
{
    AgentResponse failResponse = await agent.RunAsync(
        "What's the weather in Paris?",
        failSession,
        cancellationToken: cts.Token);

    Console.WriteLine($"Agent (during failure): {failResponse.Text}\n");
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n[Cancelled by user]");
    return;
}

WeatherToolState.SimulateFailure = false;

Console.WriteLine("=== Weather Tool demo complete! ===");

// ---------------------------------------------------------------
// Tool Implementation
// ---------------------------------------------------------------

/// <summary>
/// Retrieves simulated weather data for a given city.
/// The [Description] attributes are critical — they tell the LLM
/// what the tool does and what each parameter means.
/// </summary>
/// <param name="city">The city to get weather for (e.g., "Seattle", "Tokyo").</param>
/// <param name="cancellationToken">Token to cancel the operation.</param>
/// <returns>A formatted string describing the current weather conditions.</returns>
[Description("Gets the current weather conditions for a specified city, including temperature, humidity, and description.")]
static async Task<string> GetWeatherAsync(
    [Description("The name of the city to get weather for (e.g., 'Seattle', 'Tokyo', 'London')")] string city,
    CancellationToken cancellationToken)
{
    // Simulate network latency
    await Task.Delay(100, cancellationToken);

    // Simulate API failure when enabled
    if (WeatherToolState.SimulateFailure)
    {
        return $"Error: Weather service unavailable for '{city}'. " +
               "The external API returned a 503 Service Unavailable response. " +
               "Please try again later.";
    }

    // Simulated weather data for demonstration purposes
    var weatherData = new Dictionary<string, (int TempC, int Humidity, string Condition)>(StringComparer.OrdinalIgnoreCase)
    {
        ["Seattle"] = (12, 78, "Overcast with light rain"),
        ["Tokyo"] = (22, 65, "Partly cloudy"),
        ["London"] = (14, 82, "Foggy"),
        ["Miami"] = (31, 88, "Sunny and humid"),
        ["Paris"] = (18, 70, "Clear skies"),
        ["Sydney"] = (25, 55, "Sunny"),
        ["New York"] = (20, 60, "Partly cloudy"),
    };

    if (weatherData.TryGetValue(city, out var data))
    {
        return $"Weather in {city}: {data.Condition}. " +
               $"Temperature: {data.TempC}°C ({data.TempC * 9 / 5 + 32}°F). " +
               $"Humidity: {data.Humidity}%.";
    }

    return $"Weather data for '{city}' is not available in the demo dataset. " +
           "Available cities: Seattle, Tokyo, London, Miami, Paris, Sydney, New York.";
}

/// <summary>
/// Holds mutable state for simulating weather API failures.
/// </summary>
static class WeatherToolState
{
    /// <summary>
    /// When true, the weather tool will return an error response.
    /// </summary>
    public static bool SimulateFailure { get; set; }
}
