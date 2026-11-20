using System.ComponentModel;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace Ch21.EvalSuite;

/// <summary>The agent under test and its local checks, shared by the run and the self-test.</summary>
public static partial class WeatherSuite
{
    public const string FoundrySkipped =
        "[Foundry layer skipped] To score with FoundryEvals (preview), set LlmProvider:Foundry:ProjectEndpoint\n" +
        "in appsettings.json and run `az login`. See the development-environment chapter for setup.";

    [Description("Get the current weather for a city.")]
    public static string GetWeather(string city) => city.ToLowerInvariant() switch
    {
        "seattle" => "55F, light rain",
        "portland" => "60F, partly cloudy",
        "phoenix" => "92F, sunny",
        _ => "weather data unavailable",
    };

    public static ChatClientAgent CreateAgent(IChatClient chatClient) => new(
        chatClient,
        instructions: """
            You are a concise weather assistant. When the user asks about the
            weather in a city, call the get_weather tool to fetch the current
            conditions, then reply in one short sentence that states the weather
            and temperature for that city.
            """,
        name: "WeatherAgent",
        tools: [AIFunctionFactory.Create((Delegate)GetWeather, name: "get_weather")]);

    /// <summary>The answer states a Fahrenheit temperature as a number.</summary>
    public static EvalCheck TemperatureStated =>
        FunctionEvaluator.Create("temperature_stated", (string response) => Fahrenheit().IsMatch(response));

    public static LocalEvaluator LocalChecks() => new(
        TemperatureStated,
        EvalChecks.ToolCalledCheck("get_weather"),
        FunctionEvaluator.Create("response_length", (string response) => response.Length is > 10 and < 800));

    public static void PrintResults(string label, AgentEvaluationResults r)
    {
        Console.WriteLine($"--- {label} ---");
        Console.WriteLine($"Provider: {r.ProviderName}");
        Console.WriteLine($"Passed:   {r.Passed}/{r.Total}");
        Console.WriteLine($"Failed:   {r.Failed}");
        Console.WriteLine(r.AllPassed ? "All checks green." : "Some checks failed.");

        // Items[i] lines up with InputItems[i]; a query fails when any of its check metrics does.
        IReadOnlyList<EvaluationResult> items = r.Items ?? [];
        IReadOnlyList<EvalItem> inputs = r.InputItems ?? [];
        for (int i = 0; i < items.Count && i < inputs.Count; i++)
        {
            string[] failed = [.. items[i].Metrics.Values.Where(m => m.Interpretation?.Failed == true).Select(m => m.Name)];
            Console.WriteLine($"  [{(failed.Length > 0 ? "FAIL" : "pass")}] {inputs[i].Query}" +
                              (failed.Length > 0 ? $"  ({string.Join(", ", failed)})" : ""));
        }
    }

    // A number, optional degree sign, then F as its own token: "55F", "92 F", "60°F".
    [GeneratedRegex(@"\b\d{1,3}\s?°?F\b")]
    private static partial Regex Fahrenheit();
}
