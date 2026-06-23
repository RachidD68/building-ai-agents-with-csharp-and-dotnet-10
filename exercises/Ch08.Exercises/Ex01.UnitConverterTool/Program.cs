// ============================================================================
// Exercise 01 — Unit Converter Tool (Beginner)
// Agent with unit converter tools: ConvertTemperature, ConvertDistance,
// ConvertWeight. FULLY STANDALONE.
// ============================================================================

using System.ClientModel;
using System.ComponentModel;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

using IHost host = builder.Build();
IConfiguration config = host.Services.GetRequiredService<IConfiguration>();
IChatClient chatClient = CreateChatClient(config);

Console.WriteLine("=== Exercise: Unit Converter Tool ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// Register tools
AIFunction tempTool = AIFunctionFactory.Create(ConvertTemperatureAsync);
AIFunction distTool = AIFunctionFactory.Create(ConvertDistanceAsync);
AIFunction weightTool = AIFunctionFactory.Create(ConvertWeightAsync);

ChatClientAgent agent = new(
    chatClient,
    instructions: "You are a helpful unit conversion assistant. Use the provided tools to convert units. After converting, provide helpful context about the result.",
    name: "UnitConverter",
    description: "Converts temperature, distance, and weight between units",
    tools: [tempTool, distTool, weightTool]);

string[] prompts =
[
    "Convert 100 degrees Fahrenheit to Celsius, then tell me if that's hot for a summer day.",
    "How many kilometers is a marathon (26.2 miles)?",
    "I weigh 150 pounds. What is that in kilograms?",
    "Convert 0 Celsius to Fahrenheit and 1000 meters to miles."
];

foreach (string prompt in prompts)
{
    Console.WriteLine($"You: {prompt}");

    try
    {
        AgentSession session = await agent.CreateSessionAsync(cts.Token);
        AgentResponse response = await agent.RunAsync(
            prompt, session, cancellationToken: cts.Token);

        Console.WriteLine($"Agent: {response.Text}\n");
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("\n[Cancelled]");
        return;
    }
}

Console.WriteLine("=== Exercise complete! ===");

// ---------------------------------------------------------------
// Tool implementations
// ---------------------------------------------------------------

/// <summary>
/// Converts temperature between Fahrenheit and Celsius.
/// </summary>
[Description("Converts a temperature value between Fahrenheit and Celsius.")]
static Task<string> ConvertTemperatureAsync(
    [Description("The temperature value to convert")] double value,
    [Description("The source unit: 'F' for Fahrenheit or 'C' for Celsius")] string fromUnit,
    CancellationToken cancellationToken)
{
    string normalized = fromUnit.Trim().ToUpperInvariant();
    string result = normalized switch
    {
        "F" => $"{value}°F = {(value - 32) * 5.0 / 9.0:F1}°C",
        "C" => $"{value}°C = {value * 9.0 / 5.0 + 32:F1}°F",
        _ => $"Unknown unit: {fromUnit}. Use 'F' or 'C'."
    };

    Console.WriteLine($"  [Tool] ConvertTemperature({value}, {fromUnit}) → {result}");
    return Task.FromResult(result);
}

/// <summary>
/// Converts distance between miles and kilometers.
/// </summary>
[Description("Converts a distance value between miles and kilometers.")]
static Task<string> ConvertDistanceAsync(
    [Description("The distance value to convert")] double value,
    [Description("The source unit: 'mi' for miles or 'km' for kilometers")] string fromUnit,
    CancellationToken cancellationToken)
{
    string normalized = fromUnit.Trim().ToLowerInvariant();
    string result = normalized switch
    {
        "mi" or "miles" => $"{value} miles = {value * 1.60934:F2} km",
        "km" or "kilometers" => $"{value} km = {value / 1.60934:F2} miles",
        "m" or "meters" => $"{value} meters = {value / 1609.34:F4} miles ({value / 1000.0:F2} km)",
        _ => $"Unknown unit: {fromUnit}. Use 'mi', 'km', or 'm'."
    };

    Console.WriteLine($"  [Tool] ConvertDistance({value}, {fromUnit}) → {result}");
    return Task.FromResult(result);
}

/// <summary>
/// Converts weight between pounds and kilograms.
/// </summary>
[Description("Converts a weight value between pounds and kilograms.")]
static Task<string> ConvertWeightAsync(
    [Description("The weight value to convert")] double value,
    [Description("The source unit: 'lb' for pounds or 'kg' for kilograms")] string fromUnit,
    CancellationToken cancellationToken)
{
    string normalized = fromUnit.Trim().ToLowerInvariant();
    string result = normalized switch
    {
        "lb" or "lbs" or "pounds" => $"{value} lb = {value * 0.453592:F2} kg",
        "kg" or "kilograms" => $"{value} kg = {value / 0.453592:F2} lb",
        _ => $"Unknown unit: {fromUnit}. Use 'lb' or 'kg'."
    };

    Console.WriteLine($"  [Tool] ConvertWeight({value}, {fromUnit}) → {result}");
    return Task.FromResult(result);
}

// ---------------------------------------------------------------
// Provider factory
// ---------------------------------------------------------------

/// <summary>Creates an IChatClient from configuration.</summary>
static IChatClient CreateChatClient(IConfiguration configuration)
{
    string provider = configuration["LlmProvider:Provider"]?.ToLowerInvariant() ?? "github";

    return provider switch
    {
        "azure" => new AzureOpenAIClient(
                new Uri(configuration["LlmProvider:AzureOpenAI:Endpoint"] ?? throw new InvalidOperationException("Missing endpoint")),
                new DefaultAzureCredential())
            .GetChatClient(configuration["LlmProvider:AzureOpenAI:Model"] ?? "gpt-4o-mini").AsIChatClient(),
        "openai" => new OpenAI.OpenAIClient(configuration["LlmProvider:OpenAI:ApiKey"] ?? throw new InvalidOperationException("Missing key"))
            .GetChatClient(configuration["LlmProvider:OpenAI:Model"] ?? "gpt-4o-mini").AsIChatClient(),
        "github" => new OpenAI.OpenAIClient(
                new ApiKeyCredential(configuration["LlmProvider:GitHub:Token"] ?? throw new InvalidOperationException("Missing token")),
                new OpenAI.OpenAIClientOptions { Endpoint = new Uri(configuration["LlmProvider:GitHub:Endpoint"] ?? "https://models.github.ai/inference") })
            .GetChatClient(configuration["LlmProvider:GitHub:Model"] ?? "openai/gpt-4o-mini").AsIChatClient(),
        "ollama" => new OllamaSharp.OllamaApiClient(
            new Uri(configuration["LlmProvider:Ollama:Endpoint"] ?? "http://localhost:11434"),
            configuration["LlmProvider:Ollama:Model"] ?? "llama3.2"),
        _ => throw new InvalidOperationException($"Unknown provider: '{provider}'")
    };
}
