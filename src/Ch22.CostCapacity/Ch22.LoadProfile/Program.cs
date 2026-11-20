// ============================================================================
// Stepped load until the declared limit
// ----------------------------------------------------------------------------
// Reads a versioned workload file, then runs it at each concurrency step in
// turn: the clients send their requests through admission control (model
// slots plus a bounded queue; a full queue answers busy at once). Every
// request is timed on monotonic clocks kept apart: queue, tool wait, time to
// first token, generation, total. The run stops at the first step that
// crosses the limit the workload declares, then names the last step that held,
// with the workload version, the machine and n.
//
// The runner owns no retry, and the HttpClient under the connector has no
// resilience handler: a load test that retries hides the 429s it is there to
// count. The tool is a fixed delay standing in for a policy lookup.
//
//   dotnet run                                   live: Ollama (localhost:11434, llama3.2)
//   dotnet run -- --endpoint <url> --model <name> --workload <file>
//   dotnet run -- --json-out steps.json --usage-out usage.json
//   dotnet run -- --offline                      scripted model: slow tool, 429s beyond 3 in flight
//   dotnet run -- --self-test                    offline checks, RESULT n/n
// ============================================================================

using System.Globalization;
using Ch22.LoadProfile;
using Microsoft.Extensions.AI;
using OllamaSharp;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
if (args.Contains("--self-test")) return await LoadProfileSelfTest.RunAsync();

bool offline = args.Contains("--offline");
string data = Path.Combine(AppContext.BaseDirectory, "data");
Workload workload = Workload.Load(Option("--workload")
    ?? Path.Combine(data, offline ? "workload.offline.v1.json" : "workload.contoso-claims.v1.json"));
string modelName = Option("--model") ?? "llama3.2";
TimeProvider clock = TimeProvider.System;

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
using HttpClient http = new() { BaseAddress = new Uri(Option("--endpoint") ?? "http://localhost:11434"),
                                Timeout = Timeout.InfiniteTimeSpan };   // the workload's timeout is the only one
IChatClient model = offline
    ? new ScriptedModel(clock, TimeSpan.FromMilliseconds(15), TimeSpan.FromMilliseconds(35), capacity: 3)
    : new OllamaApiClient(http, modelName);
Task Tool(CancellationToken ct) => Task.Delay(TimeSpan.FromMilliseconds(workload.ToolDelayMs), clock, ct);

Console.WriteLine("=== Stepped load ===\n");
List<string> machine = [.. RunRecord.Machine()];
if (!offline) machine.AddRange(await RunRecord.OllamaAsync(http, modelName));
else machine.Add("model: scripted (15 ms to first token, 35 ms generation, 429 beyond 3 in flight)");
machine.ForEach(m => Console.WriteLine($"  {m}"));
Console.WriteLine($"  workload {workload.Version}: {workload.Tasks.Count} tasks, {workload.RequestsPerStep} requests per step, " +
                  $"slots {workload.Service.Slots}, queue {workload.Service.QueueCapacity}, tool {workload.ToolDelayMs} ms");
DeclaredLimit l = workload.Limit;
Console.WriteLine($"  declared limit: p95 total <= {l.P95TotalMs:F0} ms, errors <= {Percentile.Pct(l.MaxErrorRate)}, " +
                  $"busy <= {Percentile.Pct(l.MaxBusyRate)}, accepted >= {Percentile.Pct(l.MinAcceptedRate)}\n");

LoadRunner runner = new(model, workload, clock, Tool);

// The first request of a run is reported on its own: averaged in, a cold
// start disappears (and one request out of twenty is below p95's rank).
TaskTiming first = await runner.RunOneAsync(workload.Tasks[0], cts.Token);
Console.WriteLine($"  first request: {first.Outcome}, total {first.Total.TotalMilliseconds:F0} ms, " +
                  $"ttft {Percentile.Ms(first.Ttft?.TotalMilliseconds)} ms, usage reported: {UsageFields(first.Usage)}\n");

Console.WriteLine(RunRecord.Header);
long started = clock.GetTimestamp();
LoadReport report = await runner.RunAsync(step => Console.WriteLine(RunRecord.Row(step)), cts.Token);
TimeSpan wall = clock.GetElapsedTime(started);
Console.WriteLine($"\n  {report.Declare(workload.Limit)}");
Console.WriteLine($"  ms; latency clocks over answered requests; wall time {wall.TotalSeconds:F1} s");

if (Option("--json-out") is { } jsonOut) File.WriteAllText(jsonOut, RunRecord.StepsJson(workload, report, machine));
if (Option("--usage-out") is { } usageOut) File.WriteAllText(usageOut, RunRecord.Usage(workload, modelName, report, wall).ToJson());
return 0;

string? Option(string name) => Array.IndexOf(args, name) is int i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

static string UsageFields(UsageDetails? u) => u is null ? "none" : string.Join(", ",
    new[] { ("input", u.InputTokenCount), ("output", u.OutputTokenCount), ("cached", u.CachedInputTokenCount),
            ("reasoning", u.ReasoningTokenCount) }.Select(f => $"{f.Item1} {f.Item2?.ToString() ?? "null"}"));
