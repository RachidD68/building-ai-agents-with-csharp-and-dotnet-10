// ============================================================================
// Exercise 02 — Traced Pipeline (Intermediate)
// Chapter 12's Content Pipeline (Researcher -> Writer -> Editor) wrapped in
// custom Activity spans: pipeline.researcher, pipeline.writer, pipeline.editor
// as SIBLINGS under one pipeline.run root span, each tagged with the stage's
// input length and output length. Each stage's actual output feeds the next
// stage. The trace exports to the console. FULLY STANDALONE.
// ============================================================================

using System.ClientModel;
using System.Diagnostics;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// ---------------------------------------------------------------
// Set up OTel tracing
// ---------------------------------------------------------------
ActivitySource activitySource = new("Ex02.TracedPipeline");

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("TracedPipeline"))
    .WithTracing(t => t
        .AddSource(activitySource.Name)
        .AddHttpClientInstrumentation()
        .AddConsoleExporter());

using IHost host = builder.Build();
IConfiguration config = host.Services.GetRequiredService<IConfiguration>();
IChatClient chatClient = CreateChatClient(config);

Console.WriteLine("=== Exercise: Traced Pipeline (Researcher -> Writer -> Editor) ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// ---------------------------------------------------------------
// The Content Pipeline agents (Chapter 12)
// ---------------------------------------------------------------
ChatClientAgent researcher = new(chatClient,
    instructions: "You are a researcher. List 4-5 key facts or points about the " +
                  "given topic as short bullet points. Return ONLY the bullet points.",
    name: "Researcher");

ChatClientAgent writer = new(chatClient,
    instructions: "You are a writer. Turn the provided research notes into one " +
                  "clear, engaging paragraph. Use ONLY the notes. Return ONLY the paragraph.",
    name: "Writer");

ChatClientAgent editor = new(chatClient,
    instructions: "You are an editor. Polish the provided draft for clarity, flow, " +
                  "and concision. Return ONLY the edited text.",
    name: "Editor");

string topic = "How OpenTelemetry tracing helps debug multi-agent pipelines";

Console.WriteLine($"Topic: {topic}\n");

// ---------------------------------------------------------------
// Execute the pipeline under one root span
// ---------------------------------------------------------------
// One outer Activity for pipeline.run, then three children inside it. The
// using-block ordering creates the parent-child relationship via
// Activity.Current: pipeline.run is Current while each stage span starts, so
// all three stages are its children — siblings at the same level, NOT nested
// inside each other (each stage's using block closes before the next opens).
using Activity? pipelineActivity = activitySource.StartActivity("pipeline.run");
pipelineActivity?.SetTag("pipeline.topic", topic);
pipelineActivity?.SetTag("input.length", topic.Length);

if (pipelineActivity is not null)
{
    Console.WriteLine($"pipeline.run: trace={pipelineActivity.TraceId} span={pipelineActivity.SpanId}\n");
}

try
{
    // Stage 1: Researcher — gathers key points on the topic.
    string research;
    using (Activity? span = activitySource.StartActivity("pipeline.researcher"))
    {
        span?.SetTag("input.length", topic.Length);

        AgentSession s1 = await researcher.CreateSessionAsync(cts.Token);
        AgentResponse r1 = await researcher.RunAsync(topic, s1, cancellationToken: cts.Token);
        research = r1.Text ?? string.Empty;

        span?.SetTag("output.length", research.Length);
        PrintStage("Researcher", span, research);
    }

    // Stage 2: Writer — drafts from the RESEARCHER'S output.
    string draft;
    using (Activity? span = activitySource.StartActivity("pipeline.writer"))
    {
        span?.SetTag("input.length", research.Length);

        AgentSession s2 = await writer.CreateSessionAsync(cts.Token);
        AgentResponse r2 = await writer.RunAsync(research, s2, cancellationToken: cts.Token);
        draft = r2.Text ?? string.Empty;

        span?.SetTag("output.length", draft.Length);
        PrintStage("Writer", span, draft);
    }

    // Stage 3: Editor — polishes the WRITER'S output.
    string edited;
    using (Activity? span = activitySource.StartActivity("pipeline.editor"))
    {
        span?.SetTag("input.length", draft.Length);

        AgentSession s3 = await editor.CreateSessionAsync(cts.Token);
        AgentResponse r3 = await editor.RunAsync(draft, s3, cancellationToken: cts.Token);
        edited = r3.Text ?? string.Empty;

        span?.SetTag("output.length", edited.Length);
        PrintStage("Editor", span, edited);
    }

    pipelineActivity?.SetTag("pipeline.status", "success");

    Console.WriteLine("Pipeline completed. In the console trace output, verify the");
    Console.WriteLine("parent-child relationships: each pipeline.* stage span carries");
    Console.WriteLine("the SAME ParentSpanId — the pipeline.run root span's id.");
}
catch (OperationCanceledException)
{
    pipelineActivity?.SetTag("pipeline.status", "cancelled");
    Console.WriteLine("\n[Cancelled]");
}

Console.WriteLine("\n=== Exercise complete! ===");

/// <summary>Prints a stage's output and its span identifiers for verification.</summary>
static void PrintStage(string stage, Activity? span, string output)
{
    Console.WriteLine($"[{stage}] {output}");
    if (span is not null)
    {
        Console.WriteLine($"    span {span.OperationName}: id={span.SpanId} parent={span.ParentSpanId}");
    }
    Console.WriteLine();
}

/// <summary>Creates an IChatClient from configuration.</summary>
static IChatClient CreateChatClient(IConfiguration configuration)
{
    string provider = configuration["LlmProvider:Provider"]?.ToLowerInvariant() ?? "github";
    return provider switch
    {
        "azure" => new AzureOpenAIClient(new Uri(configuration["LlmProvider:AzureOpenAI:Endpoint"] ?? throw new InvalidOperationException("Missing")), new DefaultAzureCredential())
            .GetChatClient(configuration["LlmProvider:AzureOpenAI:Model"] ?? "gpt-4o-mini").AsIChatClient(),
        "openai" => new OpenAI.OpenAIClient(configuration["LlmProvider:OpenAI:ApiKey"] ?? throw new InvalidOperationException("Missing"))
            .GetChatClient(configuration["LlmProvider:OpenAI:Model"] ?? "gpt-4o-mini").AsIChatClient(),
        "github" => new OpenAI.OpenAIClient(new ApiKeyCredential(configuration["LlmProvider:GitHub:Token"] ?? throw new InvalidOperationException("Missing")),
                new OpenAI.OpenAIClientOptions { Endpoint = new Uri(configuration["LlmProvider:GitHub:Endpoint"] ?? "https://models.github.ai/inference") })
            .GetChatClient(configuration["LlmProvider:GitHub:Model"] ?? "openai/gpt-4o-mini").AsIChatClient(),
        "ollama" => new OllamaSharp.OllamaApiClient(new Uri(configuration["LlmProvider:Ollama:Endpoint"] ?? "http://localhost:11434"), configuration["LlmProvider:Ollama:Model"] ?? "llama3.2"),
        _ => throw new InvalidOperationException($"Unknown provider: '{provider}'")
    };
}
