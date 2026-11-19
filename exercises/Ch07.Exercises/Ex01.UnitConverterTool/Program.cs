// ============================================================================
// Exercise 01 — Unit Converter Tool (Basic)
// Single-tool agent: ConvertTemperature(double value, string from, string to)
// converts between Celsius, Fahrenheit, and Kelvin. FULLY STANDALONE.
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

// Register the single tool — one method, three parameters.
AIFunction tempTool = AIFunctionFactory.Create(ConvertTemperature);

ChatClientAgent agent = new(
    chatClient,
    instructions: "You are a helpful temperature conversion assistant. Use the ConvertTemperature tool for every conversion instead of computing it yourself. After converting, provide helpful context about the result.",
    name: "UnitConverter",
    description: "Converts temperatures between Celsius, Fahrenheit, and Kelvin",
    tools: [tempTool]);

// The two verification questions from the chapter brief.
string[] prompts =
[
    "Convert 100°F to Celsius.",
    "What's 0°C in Kelvin?"
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
// Tool implementation
// ---------------------------------------------------------------

/// <summary>
/// Converts a temperature between Celsius, Fahrenheit, and Kelvin.
/// </summary>
[Description("Converts a temperature value between Celsius, Fahrenheit, and Kelvin.")]
static string ConvertTemperature(
    [Description("The temperature value to convert")] double value,
    [Description("The source unit. Valid units: 'Celsius', 'Fahrenheit', or 'Kelvin' (short forms 'C', 'F', 'K' also accepted)")] string from,
    [Description("The target unit. Valid units: 'Celsius', 'Fahrenheit', or 'Kelvin' (short forms 'C', 'F', 'K' also accepted)")] string to)
{
    // Normalize both units through Celsius as the pivot.
    double? celsius = Normalize(from) switch
    {
        "C" => value,
        "F" => (value - 32) * 5.0 / 9.0,
        "K" => value - 273.15,
        _ => null
    };

    if (celsius is not double c)
    {
        // Graceful degradation: return the error as a string so the agent can explain it.
        return $"Unknown source unit: '{from}'. Valid units: Celsius, Fahrenheit, Kelvin.";
    }

    string? result = Normalize(to) switch
    {
        "C" => $"{value:0.##}{Symbol(from)} = {c:0.##}°C",
        "F" => $"{value:0.##}{Symbol(from)} = {c * 9.0 / 5.0 + 32:0.##}°F",
        "K" => $"{value:0.##}{Symbol(from)} = {c + 273.15:0.##} K",
        _ => null
    };

    if (result is null)
    {
        return $"Unknown target unit: '{to}'. Valid units: Celsius, Fahrenheit, Kelvin.";
    }

    Console.WriteLine($"  [Tool] ConvertTemperature({value}, {from}, {to}) → {result}");
    return result;

    static string Normalize(string unit) => unit.Trim().ToUpperInvariant() switch
    {
        "C" or "CELSIUS" => "C",
        "F" or "FAHRENHEIT" => "F",
        "K" or "KELVIN" => "K",
        var other => other
    };

    static string Symbol(string unit) => Normalize(unit) switch
    {
        "C" => "°C",
        "F" => "°F",
        "K" => " K",
        _ => $" {unit}"
    };
}

// ---------------------------------------------------------------
// Provider factory
// ---------------------------------------------------------------

/// <summary>Creates an IChatClient from configuration.</summary>
static IChatClient CreateChatClient(IConfiguration configuration)
{
    string provider = configuration["LlmProvider:Provider"]?.ToLowerInvariant() ?? "ollama";

    return provider switch
    {
        "azure" => new AzureOpenAIClient(
                new Uri(configuration["LlmProvider:AzureOpenAI:Endpoint"] ?? throw new InvalidOperationException("Missing endpoint")),
                new DefaultAzureCredential())
            .GetChatClient(configuration["LlmProvider:AzureOpenAI:Model"] ?? "gpt-5.6-luna").AsIChatClient(),
        "openai" => new OpenAI.OpenAIClient(configuration["LlmProvider:OpenAI:ApiKey"] ?? throw new InvalidOperationException("Missing key"))
            .GetChatClient(configuration["LlmProvider:OpenAI:Model"] ?? "gpt-5.6-luna").AsIChatClient(),
        "ollama" => new OllamaSharp.OllamaApiClient(
            new Uri(configuration["LlmProvider:Ollama:Endpoint"] ?? "http://localhost:11434"),
            configuration["LlmProvider:Ollama:Model"] ?? "llama3.2"),
        _ => throw new InvalidOperationException($"Unknown provider: '{provider}'")
    };
}
