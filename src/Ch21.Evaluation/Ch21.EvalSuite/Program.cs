// ============================================================================
// Evaluation: LocalEvaluator + FunctionEvaluator + (optional)
// FoundryEvals against a small weather agent
// ----------------------------------------------------------------------------
// This sample evaluates a single agent equipped with one tool (get_weather)
// using three checks composed in a single EvaluateAsync call:
//
//   1. temperature_stated       — custom check: the answer states a temperature
//                                 in Fahrenheit ("55F", "92 F"). Not
//                                 EvalChecks.KeywordCheck("F"): the self-test
//                                 shows how little a one-letter keyword proves.
//   2. EvalChecks.ToolCalledCheck — the agent must have invoked get_weather.
//   3. response_length          — custom predicate: not empty, not runaway.
//
// If LlmProvider:Foundry:ProjectEndpoint is configured, the sample ALSO runs
// FoundryEvals(Relevance, Coherence) on the same queries with a cloud judge.
// FoundryEvals ships in Microsoft.Agents.AI.Foundry, a PREVIEW package (with
// the preview Azure.AI.Projects); the local checks need GA packages only.
// Without an endpoint the Foundry layer is skipped with a message.
//
// The exit code reflects the local pass-rate: 0 if all local checks pass,
// 1 otherwise — so this program drops into a CI step as-is.
//
//   dotnet run                    Ollama (llama3.2) answers three weather questions
//   dotnet run -- --self-test     scripted model, no network; RESULT n/n
// ============================================================================

using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Foundry;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgenticAI.Common.Extensions;
using Ch21.EvalSuite;

if (args.Contains("--self-test")) return await EvalSuiteSelfTest.RunAsync();

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);

using IHost host = builder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();
IConfiguration config = host.Services.GetRequiredService<IConfiguration>();

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine("=== Evaluation suite ===");
Console.WriteLine();

// ── The agent under test ──────────────────────────────────────────
// One tool, one well-scoped persona. Easy to evaluate because the expected
// behaviour is narrow: call the tool when asked about weather, then answer
// with the result.
ChatClientAgent agent = WeatherSuite.CreateAgent(chatClient);

// ── The evaluator: three local checks composed in one LocalEvaluator ──
LocalEvaluator local = WeatherSuite.LocalChecks();

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

WeatherSuite.PrintResults("LocalEvaluator", localResults);

// ── Optional: Foundry-judged scoring on the same queries ─────────
// Gated behind a configured Foundry project endpoint so the sample stays
// runnable offline. The project client comes from AgenticAI.Common.Foundry,
// the same handle the rest of the book uses (the development-environment chapter).
if (FoundryProjects.TryCreate(config) is not { } projectClient)
{
    Console.WriteLine();
    Console.WriteLine(WeatherSuite.FoundrySkipped);
}
else
{
    Console.WriteLine();
    Console.WriteLine("[Foundry layer ready] Endpoint configured.");

    // FoundryEvals takes the project client, the judge deployment name, then
    // the evaluator names to run.
    string judgeModel = config["LlmProvider:Foundry:Model"] ?? "gpt-5.6-terra";
    FoundryEvals foundry = new(
        projectClient,
        judgeModel,
        FoundryEvals.Relevance,
        FoundryEvals.Coherence);

    AgentEvaluationResults foundryResults = await agent.EvaluateAsync(
        queries,
        foundry,
        cancellationToken: cts.Token);

    WeatherSuite.PrintResults("FoundryEvals (Relevance + Coherence)", foundryResults);
}

Console.WriteLine();
Console.WriteLine("=== Done ===");

// ── Exit-code contract for CI ─────────────────────────────────────
// 0 if every local check passed across every query; 1 otherwise. Foundry
// results are informational and never block the build.
return localResults.Passed == localResults.Total ? 0 : 1;
