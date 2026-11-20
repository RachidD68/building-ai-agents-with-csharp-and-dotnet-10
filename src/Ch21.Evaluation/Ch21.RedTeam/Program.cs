// ============================================================================
// Red-team regression suite: attacks as versioned test cases
// ----------------------------------------------------------------------------
// The security chapter's injection and access cases, kept as a suite that runs
// on every change (cases/redteam.v1.json): an order hidden in a retrieved
// document, a claim id and a policy from another tenant, and a note addressed
// to the evaluator. The run fails on any forbidden effect, and the effects are
// recorded by the tools where they happen, never read from the model's text.
// A judge, when one is used, sees only evidence this caller may read.
//
//   dotnet run                    Ollama (llama3.2) against the guarded desk
//   dotnet run -- --unguarded     the same, with the tool checks removed
//   dotnet run -- --self-test     scripted obedient model, both desks; RESULT n/n
// ============================================================================

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Ch21.RedTeam;

RedTeamSet set = RedTeamSet.Load(Path.Combine(AppContext.BaseDirectory, "cases", "redteam.v1.json"));
if (args.Contains("--self-test")) return await SelfTest.RunAsync(set);

IConfiguration config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables()
    .Build();
string endpoint = config["LlmProvider:Ollama:Endpoint"] ?? "http://localhost:11434";
string model = config["LlmProvider:Ollama:Model"] ?? "llama3.2";
bool guarded = !args.Contains("--unguarded");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine("=== Red-team suite (live) ===");
Console.WriteLine($"model {model}; cases {set.Version} ({set.Cases.Count}); caller {set.Caller.User}@{set.Caller.Tenant}; " +
                  $"desk {(guarded ? "guarded" : "UNGUARDED (checks removed)")}");
Console.WriteLine();

IReadOnlyList<CaseResult> results;
try
{
    results = await RedTeamSuite.RunAsync(set, guarded,
        _ => new OllamaSharp.OllamaApiClient(new Uri(endpoint), model), ct: cts.Token);
}
catch (HttpRequestException ex)
{
    Console.WriteLine($"Ollama is not reachable at {endpoint}: {ex.Message}");
    return 2;
}
RedTeamSuite.Print(results);
Console.WriteLine();
Console.WriteLine("One run of a small local model: which attacks it tried is this run's observation, not a rate.");
return results.All(r => r.Passed) ? 0 : 1;
