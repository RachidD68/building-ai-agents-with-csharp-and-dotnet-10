// ============================================================================
// Exercise 02 — Provider benchmark (Intermediate)
// ----------------------------------------------------------------------------
// Sends the same prompt 5 times to every configured provider and reports, per
// provider, the time to the first streamed token and the total time. Failures
// are counted apart and never averaged in. The report is dated and names the
// package versions it ran on, because a latency figure without them means
// little. (The performance chapter measures this properly.)
// FULLY STANDALONE: no reference to AgenticAI.Common.
//
//   dotnet run -- --self-test      offline: the timing harness on a fake clock, RESULT n/n
//   dotnet run                     5 calls per configured provider
// ============================================================================

using System.Reflection;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Ex02.ProviderBenchmark;

if (args.Contains("--self-test"))
{
    return SelfTest.Run();
}

const int Calls = 5;
const string Prompt = "Name one benefit of records over classes in C#, in one sentence.";

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
IConfigurationSection llm = builder.Configuration.GetSection("LlmProvider");

// A provider counts as configured when the one field it cannot work without is set.
(string Name, string Model, Func<IChatClient> Create)[] configured =
[
    .. new (string Name, string Required, string ModelKey, Func<IChatClient> Create)[]
    {
        ("ollama", "Ollama:Endpoint", "Ollama:Model", () => new OllamaSharp.OllamaApiClient(
            new Uri(llm["Ollama:Endpoint"]!), llm["Ollama:Model"] ?? "llama3.2")),
        ("openai", "OpenAI:ApiKey", "OpenAI:Model", () => new OpenAI.OpenAIClient(llm["OpenAI:ApiKey"]!)
            .GetChatClient(llm["OpenAI:Model"] ?? "gpt-5.6-luna").AsIChatClient()),
        ("azure", "AzureOpenAI:Endpoint", "AzureOpenAI:Model", () => new AzureOpenAIClient(
                new Uri(llm["AzureOpenAI:Endpoint"]!), new DefaultAzureCredential())
            .GetChatClient(llm["AzureOpenAI:Model"] ?? "gpt-5.6-luna").AsIChatClient()),
    }
    .Where(c => !string.IsNullOrWhiteSpace(llm[c.Required]))
    .Select(c => (c.Name, llm[c.ModelKey] ?? "(default)", c.Create)),
];

Console.WriteLine("=== Provider benchmark ===");
Console.WriteLine($"Date (UTC): {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm}");
Console.WriteLine($"Runtime:    .NET {Environment.Version}");
foreach (Type t in new[] { typeof(IChatClient), typeof(OllamaSharp.OllamaApiClient), typeof(OpenAI.OpenAIClient) })
{
    Console.WriteLine($"Package:    {t.Assembly.GetName().Name} {Pin(t.Assembly)}");
}
Console.WriteLine($"Prompt:     {Prompt}  (x{Calls})\n");

if (configured.Length == 0)
{
    Console.WriteLine("No provider configured. Set LlmProvider:Ollama:Endpoint, :OpenAI:ApiKey or :AzureOpenAI:Endpoint.");
    return 1;
}

List<(string Label, BenchmarkSummary Summary)> rows = [];
foreach ((string name, string model, Func<IChatClient> create) in configured)
{
    using IChatClient client = create();
    IReadOnlyList<CallTiming> runs = await Benchmark.RunAsync(client, Prompt, Calls, TimeProvider.System);
    foreach (CallTiming run in runs.Where(r => r.Error is not null))
    {
        Console.WriteLine($"  {name}: call failed: {run.Error}");
    }
    rows.Add(($"{name} ({model})", BenchmarkSummary.From(runs)));
}

Console.WriteLine(BenchmarkSummary.Table(rows));
return 0;

static string Pin(Assembly assembly) =>
    assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
    ?? assembly.GetName().Version?.ToString() ?? "?";
