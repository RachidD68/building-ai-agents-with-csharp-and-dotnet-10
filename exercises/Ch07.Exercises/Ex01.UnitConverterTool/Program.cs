// ============================================================================
// Exercise 01: Unit Converter Tool (Basic)
// ----------------------------------------------------------------------------
// Single-tool agent: ConvertTemperature(double value, string from, string to)
// converts between Celsius, Fahrenheit and Kelvin. An unknown unit comes back
// as an error string the model can explain, never as an exception.
//
//   dotnet run                    against the configured model (Ollama llama3.2)
//   dotnet run -- --self-test     direct calls plus a scripted chat client,
//                                 no network; RESULT n/n
// ============================================================================

using System.ComponentModel;
using AgenticAI.Common.Extensions;
using AgenticAI.Testing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

if (args.Contains("--self-test")) return await SelfTest();

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);
using IHost host = builder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();

Console.WriteLine("=== Exercise: Unit Converter Tool ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

ChatClientAgent agent = Converter.CreateAgent(chatClient);

// The two verification questions from the exercise.
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
        AgentResponse response = await agent.RunAsync(prompt, session, cancellationToken: cts.Token);
        Console.WriteLine($"Agent: {response.Text}\n");
    }
    catch (OperationCanceledException) when (cts.IsCancellationRequested)
    {
        Console.WriteLine("\n[Cancelled]");
        return 0;
    }
}

Console.WriteLine("=== Exercise complete! ===");
return 0;

// ---------------------------------------------------------------
// Offline proof: dotnet run -- --self-test
// ---------------------------------------------------------------
static async Task<int> SelfTest()
{
    Console.WriteLine("=== Unit Converter Tool (self-test, no model) ===\n");
    int passed = 0, total = 0;
    void Expect(string name, bool ok) { total++; if (ok) passed++; Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}"); }

    // 1. The method on its own: no model, no agent.
    Console.WriteLine("-- direct calls --");
    string f2c = Converter.ConvertTemperature(100, "Fahrenheit", "Celsius");
    string c2k = Converter.ConvertTemperature(0, "C", "K");
    string badSource = Converter.ConvertTemperature(10, "Rankine", "Celsius");
    string badTarget = Converter.ConvertTemperature(10, "Celsius", "Rankine");
    Console.WriteLine($"   {badSource}");
    Console.WriteLine($"   {badTarget}");
    Expect("100 °F converts to 37.78 °C", f2c == "100°F = 37.78°C");
    Expect("0 °C converts to 273.15 K", c2k == "0°C = 273.15 K");
    Expect("an unknown source or target unit returns an error string naming the valid units",
        badSource.StartsWith("Unknown source unit: 'Rankine'") && badTarget.StartsWith("Unknown target unit: 'Rankine'")
        && badTarget.EndsWith("Valid units: Celsius, Fahrenheit, Kelvin."));

    // 2. Through the agent: a scripted chat client asks for the tool, then answers.
    Console.WriteLine("\n-- through the agent, scripted chat client --");
    ScriptedToolChatClient model = null!;
    model = new(turn => turn.Results.Count == 0
        ? model.Call("ConvertTemperature", new Dictionary<string, object?> { ["value"] = 100, ["from"] = "F", ["to"] = "Celsius" })
        : ScriptedToolChatClient.Say("100 °F is about 37.78 °C."));
    AgentResponse reply = await Converter.CreateAgent(model).RunAsync("Convert 100°F to Celsius.");
    ScriptedTurn first = model.Turns[0];
    AIFunction? advertised = first.Options?.Tools?.OfType<AIFunction>().SingleOrDefault();
    string Described(string parameter) => advertised is null ? "" :
        advertised.JsonSchema.GetProperty("properties").GetProperty(parameter).GetProperty("description").GetString() ?? "";
    string? result = model.Turns.Count > 1 ? model.Turns[1].Results.Single().Result?.ToString() : null;
    Console.WriteLine($"   tools advertised: {string.Join(", ", first.ToolNames)}");
    Console.WriteLine($"   tool result sent back: {result}");
    Expect("the agent advertises ConvertTemperature, with the valid units in the parameter descriptions",
        first.ToolNames.SequenceEqual(["ConvertTemperature"])
        && Described("from").Contains("Valid units: 'Celsius', 'Fahrenheit', or 'Kelvin'")
        && Described("to").Contains("Valid units: 'Celsius', 'Fahrenheit', or 'Kelvin'"));
    Expect("the tool ran with the scripted arguments (100, F, Celsius) and its result went back to the model",
        model.Turns.Count == 2 && result == "100°F = 37.78°C" && reply.Text == "100 °F is about 37.78 °C.");

    // 3. A bad unit through the agent: a result string, not an exception.
    ScriptedToolChatClient badModel = null!;
    badModel = new(turn => turn.Results.Count == 0
        ? badModel.Call("ConvertTemperature", new Dictionary<string, object?> { ["value"] = 10, ["from"] = "C", ["to"] = "Rankine" })
        : ScriptedToolChatClient.Say("Rankine isn't a unit I can convert to."));
    string? thrown = null;
    try { await Converter.CreateAgent(badModel).RunAsync("What's 10 °C in Rankine?"); }
    catch (Exception ex) { thrown = ex.GetType().Name; }
    string? badResult = badModel.Turns.Count > 1 ? badModel.Turns[1].Results.Single().Result?.ToString() : null;
    Console.WriteLine($"   bad unit, tool result sent back: {badResult}");
    Expect("an invalid unit reaches the model as an error string and the run does not throw",
        thrown is null && badResult is not null && badResult.StartsWith("Unknown target unit"));

    Console.WriteLine($"\nRESULT {passed}/{total}");
    return passed == total ? 0 : 1;
}

internal static class Converter
{
    /// <summary>The agent the live run and the self-test share.</summary>
    public static ChatClientAgent CreateAgent(IChatClient chatClient) =>
        new(chatClient,
            instructions: "You are a helpful temperature conversion assistant. Use the ConvertTemperature tool for every conversion instead of computing it yourself. After converting, provide helpful context about the result.",
            name: "UnitConverter",
            description: "Converts temperatures between Celsius, Fahrenheit, and Kelvin",
            tools: [AIFunctionFactory.Create(ConvertTemperature)]);

    /// <summary>Converts a temperature between Celsius, Fahrenheit, and Kelvin.</summary>
    [Description("Converts a temperature value between Celsius, Fahrenheit, and Kelvin.")]
    public static string ConvertTemperature(
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
    }

    private static string Normalize(string unit) => unit.Trim().ToUpperInvariant() switch
    {
        "C" or "CELSIUS" => "C",
        "F" or "FAHRENHEIT" => "F",
        "K" or "KELVIN" => "K",
        var other => other
    };

    private static string Symbol(string unit) => Normalize(unit) switch
    {
        "C" => "°C",
        "F" => "°F",
        "K" => " K",
        _ => $" {unit}"
    };
}
