// ============================================================================
// Chapter 15 — Observability and Debugging
// Demonstrates all three observability pillars for AI agents:
//   1. Logging    — Structured logging with ILogger
//   2. Metrics    — Custom metrics via System.Diagnostics.Metrics
//   3. Traces     — Distributed tracing via OpenTelemetry
//
// Plus the production essentials layered on top:
//   • A config-driven exporter switch (Console | Otlp | AzureMonitor).
//   • A configurable trace sampler (Otel:TraceSampleRatio).
//   • EnableSensitiveData wired to the environment (dev/test only).
//   • A cost-as-a-signal metric (agent.cost.usd) from a pricing table.
//
// OpenTelemetryAgent (MAF) wraps the AIAgent to emit GenAI spans and — because
// it auto-wires the inner IChatClient — model-level spans too, from one wrap.
// ============================================================================

using System.Diagnostics;
using Azure.Monitor.OpenTelemetry.Exporter;
using Ch15.OpenTelemetryAgent;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using AgenticAI.Common.Extensions;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

// ---------------------------------------------------------------
// Step 1: Read observability configuration
// ---------------------------------------------------------------
// Exporter: Console (zero-config default), Otlp (Aspire/collector), or
// AzureMonitor (Application Insights). The sample runs unchanged on Console.
string exporter = builder.Configuration["Otel:Exporter"] ?? "Console";

// Sample ratio for traces only (1.0 = every trace). Metrics and logs are
// never sampled — you always want every counter increment and every error.
double sampleRatio = double.TryParse(
    builder.Configuration["Otel:TraceSampleRatio"], out double r) ? r : 1.0;

// Pricing table for the cost metric (USD per 1,000,000 tokens). Never hard-code
// rates in production — prices move. These defaults mirror a small chat model.
double inputRate = double.TryParse(
    builder.Configuration["Pricing:InputPerMillion"], out double ir) ? ir : 0.15;
double outputRate = double.TryParse(
    builder.Configuration["Pricing:OutputPerMillion"], out double or) ? or : 0.60;

// Azure Monitor connection string is read from config / environment, never
// baked into the binary.
string? appInsightsConnString =
    builder.Configuration["APPLICATION_INSIGHTS_CONNECTION_STRING"]
    ?? builder.Configuration["ApplicationInsights:ConnectionString"];

// ---------------------------------------------------------------
// Step 2: Configure OpenTelemetry (traces + metrics)
// ---------------------------------------------------------------
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        tracing
            // Custom ActivitySource for our own agent.run spans.
            .AddSource(AgentMetrics.ActivitySourceName)
            // MAF's OpenTelemetryAgent emits spans under this default source name
            // when you don't pass an explicit sourceName. Subscribe to it or the
            // GenAI spans never reach the exporter (silent, empty dashboard).
            .AddSource("Experimental.Microsoft.Agents.AI")
            .AddHttpClientInstrumentation()
            // Sample traces only. ParentBased keeps a child span's sampling
            // decision consistent with its parent across service boundaries.
            .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(sampleRatio)));

        ApplyTraceExporter(tracing, exporter, appInsightsConnString);
    })
    .WithMetrics(metrics =>
    {
        metrics
            .AddMeter(AgentMetrics.MeterName)
            .AddHttpClientInstrumentation();

        ApplyMetricExporter(metrics, exporter, appInsightsConnString);
    });

// ---------------------------------------------------------------
// Step 3: Register services
// ---------------------------------------------------------------
builder.Services.AddLlmChatClient(builder.Configuration);
builder.Services.AddSingleton(new AgentMetrics(inputRate, outputRate));

using IHost host = builder.Build();

// Start the host so the OpenTelemetry hosted service installs its
// ActivityListener / MeterListener. Without this, StartActivity returns null
// and the exporter stays silent — the empty-dashboard failure this chapter warns
// about. We Start/Stop rather than Run because this is a fixed batch, not a server.
await host.StartAsync();

IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();
AgentMetrics metrics = host.Services.GetRequiredService<AgentMetrics>();
ILogger logger = host.Services.GetRequiredService<ILoggerFactory>()
    .CreateLogger("Ch15.OpenTelemetryAgent");

Console.WriteLine("=== Chapter 15: Observability — OpenTelemetry Agent ===");
Console.WriteLine($"Exporter: {exporter} | Trace sample ratio: {sampleRatio:0.##}");
Console.WriteLine();

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// ---------------------------------------------------------------
// Step 4: Create an instrumented agent
// ---------------------------------------------------------------
// Wrapping the agent in OpenTelemetryAgent emits agent-level spans AND, because
// it auto-wires the inner IChatClient, model-level (gen_ai.*) spans — from one
// wrap. EnableSensitiveData captures raw prompts/responses; turn it on ONLY in
// dev/test — it includes message content, function-call args, and results.
ChatClientAgent inner = new(
    chatClient,
    instructions: """
        You are a concise technical assistant. Answer in 2-3 sentences maximum.
        Focus on accuracy and brevity.
        """,
    name: "ObservableAgent");

AIAgent agent = inner
    .AsBuilder()
    .UseOpenTelemetry(
        sourceName: AgentMetrics.ActivitySourceName,
        configure: otel => otel.EnableSensitiveData = builder.Environment.IsDevelopment())
    .Build();

// ---------------------------------------------------------------
// Step 5: Run several interactions to generate telemetry
// ---------------------------------------------------------------
string[] prompts =
[
    "What is OpenTelemetry in one sentence?",
    "Explain the three pillars of observability.",
    "How does distributed tracing work in .NET?"
];

int totalRequests = 0;
long totalInputTokens = 0;
long totalOutputTokens = 0;
double totalCost = 0;
Stopwatch totalStopwatch = Stopwatch.StartNew();

try
{
    foreach (string prompt in prompts)
    {
        // Start a custom trace span for each agent interaction.
        using Activity? activity = metrics.ActivitySource.StartActivity(
            "agent.run",
            ActivityKind.Internal);

        activity?.SetTag("agent.name", agent.Name);
        activity?.SetTag("agent.prompt_length", prompt.Length);

        logger.LogInformation("Sending prompt to agent {AgentName} ({PromptLength} chars)",
            agent.Name, prompt.Length);

        Stopwatch sw = Stopwatch.StartNew();

        AgentSession session = await agent.CreateSessionAsync(cts.Token);
        AgentResponse response = await agent.RunAsync(
            prompt,
            session,
            cancellationToken: cts.Token);

        sw.Stop();

        // Record metrics.
        long inputTokens = response.Usage?.InputTokenCount ?? 0;
        long outputTokens = response.Usage?.OutputTokenCount ?? 0;

        metrics.RecordRequest(sw.Elapsed.TotalMilliseconds, agent.Name ?? "agent");
        metrics.RecordTokenUsage(inputTokens, outputTokens, agent.Name ?? "agent");
        double cost = metrics.RecordCost(inputTokens, outputTokens, agent.Name ?? "agent");

        totalRequests++;
        totalInputTokens += inputTokens;
        totalOutputTokens += outputTokens;
        totalCost += cost;

        // Set span attributes for tracing.
        activity?.SetTag("agent.input_tokens", inputTokens);
        activity?.SetTag("agent.output_tokens", outputTokens);
        activity?.SetTag("agent.cost_usd", cost);
        activity?.SetTag("agent.duration_ms", sw.Elapsed.TotalMilliseconds);
        activity?.SetStatus(ActivityStatusCode.Ok);

        logger.LogInformation(
            "Agent {AgentName} responded in {DurationMs:F0}ms " +
            "(input: {InputTokens}, output: {OutputTokens} tokens, ${Cost:F6})",
            agent.Name, sw.Elapsed.TotalMilliseconds, inputTokens, outputTokens, cost);

        // Display the interaction.
        Console.WriteLine($"\nYou: {prompt}");
        Console.WriteLine($"{agent.Name}: {response.Text}");
        Console.WriteLine($"  [{sw.Elapsed.TotalMilliseconds:F0}ms, " +
                          $"{inputTokens} in / {outputTokens} out tokens, ${cost:F6}]");

        // Print the trace span info.
        if (activity is not null)
        {
            Console.WriteLine();
            Console.WriteLine(TraceFormatter.FormatActivity(activity));
        }
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n[Cancelled by user]");
}

totalStopwatch.Stop();

// ---------------------------------------------------------------
// Step 5b: Close the loop — evaluation as a metric (the fourth signal)
// ---------------------------------------------------------------
// Traces, metrics, and logs tell you the call ran; only evaluation tells you the
// answer was GOOD. We borrow the simplest evaluator — a LocalEvaluator with one
// keyword check (offline, no API key) — to turn quality into a number and record
// it on the SAME Meter as latency and cost (agent.eval.pass_rate). The evaluator
// API itself (the full check catalog, LLM-as-judge, multi-agent eval) is Chapter
// 17; here we only need a pass rate to put on the dashboard.
try
{
    // The built-in KeywordCheck verifies the response mentions the topic — the
    // simplest "did it stay on subject" gate. Chapter 18 has the full catalog:
    // tool-call checks, custom predicates, and LLM-as-judge scoring.
    LocalEvaluator evaluator = new(EvalChecks.KeywordCheck("telemetry"));

    AgentEvaluationResults eval = await agent.EvaluateAsync(
        new[]
        {
            "Define OpenTelemetry in one sentence.",
            "In one sentence, what is telemetry?",
        },
        evaluator,
        cancellationToken: cts.Token);

    double passRate = eval.Total == 0 ? 0.0 : (double)eval.Passed / eval.Total;
    metrics.RecordEvalPassRate(passRate, agent.Name ?? "agent");

    Console.WriteLine();
    Console.WriteLine($"[eval] {eval.Passed}/{eval.Total} checks passed " +
                      $"-> agent.eval.pass_rate = {passRate:P0}");
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n[eval cancelled]");
}

// ---------------------------------------------------------------
// Step 6: Print telemetry summary
// ---------------------------------------------------------------
TraceFormatter.PrintSummary(
    totalRequests,
    totalInputTokens,
    totalOutputTokens,
    totalStopwatch.Elapsed.TotalMilliseconds,
    totalCost);

Console.WriteLine("\n=== OpenTelemetry Agent demo complete! ===");
Console.WriteLine("Note: OpenTelemetry console exporter output appears above");
Console.WriteLine("in production, set Otel:Exporter to Otlp (Aspire) or AzureMonitor.");

// Stop the host so the tracer and meter providers flush their final batch to
// the console exporter before the process exits.
await host.StopAsync();

// ---------------------------------------------------------------
// Exporter wiring (config-driven). Console is the zero-config default.
// ---------------------------------------------------------------
static void ApplyTraceExporter(
    TracerProviderBuilder tracing, string exporter, string? connString)
{
    switch (exporter.ToLowerInvariant())
    {
        case "otlp":
            // Chapter 14's Aspire dashboard ingests OTLP on this endpoint.
            tracing.AddOtlpExporter(o => o.Endpoint = new Uri("http://localhost:4317"));
            break;

        case "azuremonitor":
            tracing.AddAzureMonitorTraceExporter(o => o.ConnectionString = connString);
            break;

        default:
            tracing.AddConsoleExporter();
            break;
    }
}

static void ApplyMetricExporter(
    MeterProviderBuilder metrics, string exporter, string? connString)
{
    switch (exporter.ToLowerInvariant())
    {
        case "otlp":
            metrics.AddOtlpExporter(o => o.Endpoint = new Uri("http://localhost:4317"));
            break;

        case "azuremonitor":
            metrics.AddAzureMonitorMetricExporter(o => o.ConnectionString = connString);
            break;

        default:
            metrics.AddConsoleExporter();
            break;
    }
}
