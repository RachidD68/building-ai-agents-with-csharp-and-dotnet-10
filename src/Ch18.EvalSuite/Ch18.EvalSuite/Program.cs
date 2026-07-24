// ============================================================================
// Chapter 18 — Evaluation: LocalEvaluator + FunctionEvaluator + (optional)
// FoundryEvals against a small weather agent
// ----------------------------------------------------------------------------
// This sample evaluates a single agent equipped with one tool (get_weather)
// using three layers of checks composed in a single EvaluateAsync call:
//
//   1. EvalChecks.KeywordCheck     — response must contain the temperature
//                                    unit "F" (KeywordCheck is match-ALL).
//   2. EvalChecks.ToolCalledCheck  — agent must have invoked get_weather.
//   3. FunctionEvaluator.Create    — custom predicate: response is a sensible
//                                    length (not empty, not runaway).
//
// If LlmProvider:Foundry:ProjectEndpoint is configured, the sample ALSO runs
// FoundryEvals(Relevance, Coherence) on the same queries via the LLM judge and
// prints both result sets. Otherwise it skips the Foundry layer with a clear
// note, so the suite runs fully offline against the local model.
//
// The exit code reflects the local pass-rate: 0 if all local checks pass,
// 1 otherwise — so this program drops into a CI step as-is.
// ============================================================================

using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Foundry;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgenticAI.Common.Extensions;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);

using IHost host = builder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();
IConfiguration config = host.Services.GetRequiredService<IConfiguration>();

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine("=== Chapter 18: Evaluation suite ===");
Console.WriteLine();

// ── The agent under test ──────────────────────────────────────────
// One tool, one well-scoped persona. Easy to evaluate because the expected
// behaviour is narrow: call the tool when asked about weather, then answer
// with the result.
[System.ComponentModel.Description("Get the current weather for a city.")]
static string GetWeather(string city) => city.ToLowerInvariant() switch
{
    "seattle" => "55F, light rain",
    "portland" => "60F, partly cloudy",
    "phoenix" => "92F, sunny",
    _ => "weather data unavailable"
};

AIFunction weatherTool = AIFunctionFactory.Create((Delegate)GetWeather, name: "get_weather");

ChatClientAgent agent = new(
    chatClient,
    instructions: """
        You are a concise weather assistant. When the user asks about the
        weather in a city, call the get_weather tool to fetch the current
        conditions, then reply in one short sentence that states the weather
        and temperature for that city.
        """,
    name: "WeatherAgent",
    tools: [weatherTool]);

// ── The evaluator: three local checks composed in one LocalEvaluator ──
// KeywordCheck is match-ALL: the response must contain EVERY keyword you pass,
// not any one of them. So keep it to signals that show up in every answer —
// here, the temperature unit "F", which the tool always returns and the agent
// always surfaces. (Pass several keywords only when all of them must appear.)
LocalEvaluator local = new(
    EvalChecks.KeywordCheck("F"),
    EvalChecks.ToolCalledCheck("get_weather"),
    FunctionEvaluator.Create("response_length",
        (string response) => response.Length is > 10 and < 800));

// ── Queries to run ────────────────────────────────────────────────
string[] queries =
[
    "What's the weather in Seattle right now?",
    "How about Portland?",
    "Is it hot in Phoenix today?",
];

Console.WriteLine($"Evaluating {queries.Length} queries with the LocalEvaluator (3 checks each)...");
Console.WriteLine();

AgentEvaluationResults localResults = await agent.EvaluateAsync(
    queries,
    local,
    cancellationToken: cts.Token);

PrintResults("LocalEvaluator", localResults);

// ── Optional: Foundry-judged scoring on the same queries ─────────
// Gated behind a configured Foundry project endpoint so the sample stays
// runnable offline. When Foundry IS configured, the LLM judge scores the same
// queries for Relevance + Coherence and the two result sets print together.
string? foundryEndpoint = config["LlmProvider:Foundry:ProjectEndpoint"];
if (string.IsNullOrWhiteSpace(foundryEndpoint))
{
    Console.WriteLine();
    Console.WriteLine("[Foundry layer skipped] To enable LLM-as-judge scoring with");
    Console.WriteLine("FoundryEvals, set LlmProvider:Foundry:ProjectEndpoint in");
    Console.WriteLine("appsettings.json and run `az login`. See Chapter 4 for setup.");
}
else
{
    Console.WriteLine();
    Console.WriteLine($"[Foundry layer ready] Endpoint configured: {foundryEndpoint}");

    // The real wiring: FoundryEvals takes an AIProjectClient, the judge
    // deployment name, then the evaluator names to run. The project client is
    // the same Microsoft Foundry handle the rest of the book uses (Chapter 4).
    AIProjectClient projectClient = new(new Uri(foundryEndpoint), new DefaultAzureCredential());
    string judgeModel = config["LlmProvider:Foundry:Model"] ?? "gpt-4o";

    FoundryEvals foundry = new(
        projectClient,
        judgeModel,
        FoundryEvals.Relevance,
        FoundryEvals.Coherence);

    AgentEvaluationResults foundryResults = await agent.EvaluateAsync(
        queries,
        foundry,
        cancellationToken: cts.Token);

    PrintResults("FoundryEvals (Relevance + Coherence)", foundryResults);
}

Console.WriteLine();
Console.WriteLine("=== Done ===");

// ── Exit-code contract for CI ─────────────────────────────────────
// 0 if every local check passed across every query; 1 otherwise. Foundry
// results are informational and never block the build.
return localResults.Passed == localResults.Total ? 0 : 1;

// ──────────────────────────────────────────────────────────────────
static void PrintResults(string label, AgentEvaluationResults r)
{
    Console.WriteLine($"--- {label} ---");
    Console.WriteLine($"Provider: {r.ProviderName}");
    Console.WriteLine($"Passed:   {r.Passed}/{r.Total}");
    Console.WriteLine($"Failed:   {r.Failed}");
    Console.WriteLine(r.AllPassed ? "All checks green." : "Some checks failed.");

    // Per-query breakdown from the MEAI result list: Items[i] lines up with
    // InputItems[i], and a query fails when any of its check metrics does.
    IReadOnlyList<EvaluationResult> items = r.Items ?? [];
    IReadOnlyList<EvalItem> inputs = r.InputItems ?? [];
    for (int i = 0; i < items.Count && i < inputs.Count; i++)
    {
        bool failed = items[i].Metrics.Values.Any(m => m.Interpretation?.Failed == true);
        Console.WriteLine($"  [{(failed ? "FAIL" : "pass")}] {inputs[i].Query}");
    }
}
