using AgenticAI.Common.Extensions;
using AgenticAI.Testing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace Ch21.EvalSuite;

/// <summary>
/// Offline checks: dotnet run -- --self-test. A scripted client plays the model
/// (calls get_weather, or answers without it), so the checks are exercised
/// through the real agent loop with no network.
/// </summary>
internal static class EvalSuiteSelfTest
{
    public static async Task<int> RunAsync()
    {
        Console.WriteLine("=== Evaluation suite: self-test (scripted model, no network) ===");
        int passed = 0, total = 0;
        void Check(bool ok, string name)
        {
            total++;
            if (ok) passed++;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
        }

        string[] queries = ["What's the weather in Seattle right now?", "Is it hot in Phoenix today?"];

        Console.WriteLine("\n-- 1. A model that calls the tool and reports it --");
        AgentEvaluationResults good = await WeatherSuite.CreateAgent(UsesTool()).EvaluateAsync(queries, WeatherSuite.LocalChecks());
        WeatherSuite.PrintResults("LocalEvaluator", good);
        Check(good.AllPassed && good.Total == 2, "both answers pass temperature_stated, ToolCalledCheck and response_length");

        Console.WriteLine("\n-- 2. A model that answers without the tool --");
        AgentEvaluationResults guess = await WeatherSuite.CreateAgent(Guesses()).EvaluateAsync(queries, WeatherSuite.LocalChecks());
        WeatherSuite.PrintResults("LocalEvaluator", guess);
        Check(guess.Failed == 2, "no get_weather call and no temperature: both items fail");

        Console.WriteLine("\n-- 3. What a one-letter keyword proves --");
        const string NoWeather = "Fog maybe? I could not reach the weather service.";
        AgentResponse[] stored = [new(new ChatMessage(ChatRole.Assistant, NoWeather))];
        AgentEvaluationResults keyword = await WeatherSuite.CreateAgent(UsesTool())
            .EvaluateAsync(stored, [queries[0]], new LocalEvaluator(EvalChecks.KeywordCheck("F")));
        AgentEvaluationResults stated = await WeatherSuite.CreateAgent(UsesTool())
            .EvaluateAsync(stored, [queries[0]], new LocalEvaluator(WeatherSuite.TemperatureStated));
        Console.WriteLine($"  \"{NoWeather}\"  KeywordCheck(\"F\") {(keyword.AllPassed ? "pass" : "FAIL")}, temperature_stated {(stated.AllPassed ? "pass" : "FAIL")}");
        Check(keyword.AllPassed, "DEFECT REPRODUCED: KeywordCheck(\"F\") passes an answer with no temperature (the F of \"Fog\")");
        Check(!stated.AllPassed, "temperature_stated fails it");

        Console.WriteLine("\n-- 4. The Foundry layer without an endpoint --");
        IConfiguration empty = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["LlmProvider:Foundry:ProjectEndpoint"] = "" }).Build();
        Check(FoundryProjects.TryCreate(empty) is null && WeatherSuite.FoundrySkipped.StartsWith("[Foundry layer skipped]", StringComparison.Ordinal),
            "no endpoint: no project client, the run prints the skip note and scores locally only");

        Console.WriteLine($"\nRESULT {passed}/{total}");
        return passed == total ? 0 : 1;
    }

    /// <summary>Calls get_weather for the city in the question, then repeats the tool's result.</summary>
    private static ScriptedToolChatClient UsesTool()
    {
        ScriptedToolChatClient? model = null;
        model = new ScriptedToolChatClient(turn =>
        {
            string question = turn.Messages.Last(m => m.Role == ChatRole.User).Text;
            string city = question.Contains("Phoenix", StringComparison.Ordinal) ? "Phoenix" : "Seattle";
            return turn.Results.Count == 0
                ? model!.Call("get_weather", new Dictionary<string, object?> { ["city"] = city })
                : ScriptedToolChatClient.Say($"It is {turn.Results[^1].Result} in {city}.");
        });
        return model;
    }

    /// <summary>Answers from nothing, never calls the tool.</summary>
    private static ScriptedToolChatClient Guesses() =>
        new(_ => ScriptedToolChatClient.Say("It is probably mild and dry there today."));
}
