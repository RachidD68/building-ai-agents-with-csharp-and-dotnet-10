// ============================================================================
// Capability Profile: run one Contoso task set against several model
// configurations and write a dated profile (JSON + Markdown) of what each one
// did: correct, wrong, invalid, refused, truncated, transport and provider
// errors, kept apart, plus what the responses show about each option sent.
//
//   dotnet run -- --self-test            scripted fake client, no network, RESULT n/n
//   dotnet run                           the local configurations (Ollama), 3 trials per task
//   dotnet run -- --trials 5             more trials
//   dotnet run -- --include-cloud        adds the OpenAI configuration (needs a key; costs money)
// ============================================================================

using AgenticAI.Common.Configuration;
using AgenticAI.Common.Providers;
using Ch02.CapabilityProfile;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

if (args.Contains("--self-test")) return await SelfTest.RunAsync();

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
LlmSettings settings = new();
builder.Configuration.GetSection("LlmProvider").Bind(settings);
int trials = args.SkipWhile(a => a != "--trials").Skip(1).Select(int.Parse).FirstOrDefault(3);

using HttpClient ollamaHttp = new() { Timeout = TimeSpan.FromMinutes(2) };
IChatClient ollama = new OllamaChatClientProvider().CreateChatClient(ollamaHttp, settings);
string local = settings.Ollama.Model;

// The configurations are data: the same task set, different options.
List<ProfileConfiguration> configurations =
[
    new("local-json", "ollama", local, ollama,
        new ChatOptions { Temperature = 0f, MaxOutputTokens = 64, ResponseFormat = ChatResponseFormat.Json }),
    new("local-reasoning", "ollama", local, ollama,
        new ChatOptions { MaxOutputTokens = 64, ResponseFormat = ChatResponseFormat.Json,
                          Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Low } }),
    new("local-capped-text", "ollama", local, ollama,
        new ChatOptions { Temperature = 0.7f, MaxOutputTokens = 4 }),
];
List<string> notRun = [];

using HttpClient cloudHttp = new() { Timeout = TimeSpan.FromMinutes(2) };
OpenAIChatClientProvider openAi = new();
if (!args.Contains("--include-cloud"))
    notRun.Add("cloud-json (openai): not run; pass --include-cloud with LlmProvider:OpenAI:ApiKey set");
else if (openAi.FindMissingSetting(settings) is { } missing)
    notRun.Add($"cloud-json (openai): not run; {missing} is not set");
else
    configurations.Add(new("cloud-json", "openai", settings.OpenAI.Model, openAi.CreateChatClient(cloudHttp, settings),
        new ChatOptions { Temperature = 0f, MaxOutputTokens = 64, ResponseFormat = ChatResponseFormat.Json }));

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

DateTimeOffset started = DateTimeOffset.UtcNow;
List<ConfigurationResult> results = [];
foreach (ProfileConfiguration configuration in configurations)
{
    Console.WriteLine($"Profiling {configuration.Name} ({configuration.Provider} {configuration.Model}), {trials} trial(s) per task...");
    results.Add(await Profiler.RunAsync(configuration, ContosoTasks.All, trials, cts.Token));
}

CapabilityReport report = new(started,
    CapabilityReport.CurrentPins(typeof(OllamaSharp.OllamaApiClient), typeof(OpenAI.OpenAIClient)),
    trials, results, notRun);
(string json, string markdown) = report.Write(Path.Combine(Directory.GetCurrentDirectory(), "profiles"));

Console.WriteLine();
Console.WriteLine(report.ToMarkdown());
Console.WriteLine($"Wrote {json}");
Console.WriteLine($"Wrote {markdown}");
return 0;
