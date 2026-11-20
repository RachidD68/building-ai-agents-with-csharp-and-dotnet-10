// ============================================================================
// OpenTelemetry for an agent: traces, bounded metrics, logs without content
// ----------------------------------------------------------------------------
// Two UseOpenTelemetry calls instrument the Contoso assistant:
//   - on the ChatClientBuilder: one "chat" span per model call and one
//     "execute_tool" span per tool call, plus the gen_ai.client.* metrics;
//   - on the AIAgentBuilder: one "invoke_agent" span per run.
// The exporter wiring is the hosting chapter's ServiceDefaults (console, or
// OTLP when OTEL_EXPORTER_OTLP_ENDPOINT is set, as the Aspire dashboard does).
// This project adds the sources and meters that wiring does not know about.
//
// The names are read from what MAF 1.28 and Microsoft.Extensions.AI 10.11
// emit, not from the semantic-conventions page: the self-test asserts them.
// The framework sources still carry an "Experimental." prefix; treat every
// dashboard key built on them as subject to change.
//
//   dotnet run                    one question to the configured model, then
//                                 the trace as a tree (console exporter above it)
//   dotnet run -- --self-test     offline: span names and attributes, source
//                                 subscription, sensitive data, cardinality,
//                                 workflow spans, retry owners
// ============================================================================

using System.Diagnostics;
using AgenticAI.Common.Extensions;
using Azure.Monitor.OpenTelemetry.Exporter;
using Ch17.AspireOrchestration.ServiceDefaults;
using Ch19.Observability;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

if (args.Contains("--self-test")) return await SelfTest.RunAsync();
if (args.Contains("--sensitive-probe")) return await SelfTest.SensitiveProbeAsync();   // child of --self-test

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
// Registered first, so it runs before every exporter, the ServiceDefaults ones included.
builder.Services.AddOpenTelemetry().WithTracing(t => t.AddProcessor(new StatusDescriptionRedactor()));
builder.AddServiceDefaults();

double ratio = double.TryParse(builder.Configuration["Otel:TraceSampleRatio"], out double r) ? r : 1.0;
SpanCapture spans = new();   // a copy of every exported span, to print the tree at the end
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t
        .AddSource(ContosoTelemetry.Sources)
        .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(ratio)))
        .AddProcessor(new SimpleActivityExportProcessor(spans)))
    .WithMetrics(m => m.AddMeter(ContosoTelemetry.Meters));
// Azure Monitor (Application Insights) when a connection string is configured.
// Compiled here, not run: this environment has no Azure credentials.
string? appInsights = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
if (!string.IsNullOrWhiteSpace(appInsights))
{
    builder.Services.AddOpenTelemetry()
        .WithTracing(t => t.AddAzureMonitorTraceExporter(o => o.ConnectionString = appInsights))
        .WithMetrics(m => m.AddAzureMonitorMetricExporter(o => o.ConnectionString = appInsights));
}
builder.Services.AddLlmChatClient(builder.Configuration);
builder.Services.AddSingleton<ContosoTelemetry>();

using IHost host = builder.Build();
await host.StartAsync();   // installs the listeners; without it nothing is recorded

ContosoTelemetry telemetry = host.Services.GetRequiredService<ContosoTelemetry>();
ILoggerFactory loggers = host.Services.GetRequiredService<ILoggerFactory>();
AIAgent agent = ContosoAssistant.Create(host.Services.GetRequiredService<IChatClient>(), telemetry, loggers);

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine("=== OpenTelemetry agent ===");
const string Question = "What is the meals limit for a client dinner?";
Console.WriteLine($"You: {Question}");
AgentResponse response = await ContosoAssistant.AskAsync(agent, telemetry,
    loggers.CreateLogger("Contoso.Assistant"), "conv-" + Guid.NewGuid().ToString("N")[..8], Question, cts.Token);
Console.WriteLine($"{ContosoAssistant.AgentName}: {response.Text}");

await host.StopAsync();   // flushes the last batch to the exporters

Console.WriteLine("\n--- trace tree (from the exported spans) ---");
List<SpanRecord> all = [.. spans.Spans];
string[] keys = ["gen_ai.provider.name", "gen_ai.request.model", "gen_ai.usage.input_tokens",
                 "gen_ai.usage.output_tokens", "gen_ai.response.finish_reasons", "gen_ai.tool.name", "error.type"];
foreach (SpanRecord root in all.Where(s => all.All(p => p.SpanId != s.ParentSpanId))) Print(root, 0);
return 0;

void Print(SpanRecord s, int depth)
{
    string attrs = string.Join(" ", keys.Where(s.Tags.ContainsKey).Select(k => $"{k}={s.Tags[k]}"));
    string status = s.Status == ActivityStatusCode.Error ? "[ERROR] " : "";
    Console.WriteLine($"{new string(' ', depth * 2)}{s.Name} {status}{attrs}");
    foreach (SpanRecord child in all.Where(c => c.ParentSpanId == s.SpanId)) Print(child, depth + 1);
}
