// ============================================================================
// Exercise 02 — Traced Pipeline (Intermediate)
// Multi-agent pipeline with OpenTelemetry tracing. FULLY STANDALONE.
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

Console.WriteLine("=== Exercise: Traced Pipeline ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// Define pipeline agents
ChatClientAgent translator = new(chatClient,
    instructions: "Translate the given text to English. Return ONLY the translation.",
    name: "Translator");

ChatClientAgent summarizer = new(chatClient,
    instructions: "Summarize the given text in one sentence. Return ONLY the summary.",
    name: "Summarizer");

ChatClientAgent classifier = new(chatClient,
    instructions: "Classify the topic of the given text. Return ONLY: Topic: <topic>",
    name: "Classifier");

string input = "L'intelligence artificielle transforme le developpement logiciel avec des agents autonomes et des outils de generation de code.";

Console.WriteLine($"Input: {input}\n");

// ---------------------------------------------------------------
// Execute pipeline with tracing
// ---------------------------------------------------------------
using Activity? pipelineActivity = activitySource.StartActivity("pipeline.execute");
pipelineActivity?.SetTag("input.length", input.Length);

try
{
    // Stage 1: Translate
    string translated;
    using (Activity? translateActivity = activitySource.StartActivity("agent.translate"))
    {
        AgentSession s1 = await translator.CreateSessionAsync(cts.Token);
        AgentResponse r1 = await translator.RunAsync(input, s1, cancellationToken: cts.Token);
        translated = r1.Text ?? string.Empty;
        translateActivity?.SetTag("output.length", translated.Length);
        Console.WriteLine($"[Translated] {translated}");
    }

    // Stage 2: Summarize
    string summarized;
    using (Activity? summarizeActivity = activitySource.StartActivity("agent.summarize"))
    {
        AgentSession s2 = await summarizer.CreateSessionAsync(cts.Token);
        AgentResponse r2 = await summarizer.RunAsync(translated, s2, cancellationToken: cts.Token);
        summarized = r2.Text ?? string.Empty;
        summarizeActivity?.SetTag("output.length", summarized.Length);
        Console.WriteLine($"[Summarized] {summarized}");
    }

    // Stage 3: Classify
    string classified;
    using (Activity? classifyActivity = activitySource.StartActivity("agent.classify"))
    {
        AgentSession s3 = await classifier.CreateSessionAsync(cts.Token);
        AgentResponse r3 = await classifier.RunAsync(translated, s3, cancellationToken: cts.Token);
        classified = r3.Text ?? string.Empty;
        classifyActivity?.SetTag("output.length", classified.Length);
        Console.WriteLine($"[Classified] {classified}");
    }

    pipelineActivity?.SetTag("pipeline.status", "success");
    Console.WriteLine("\nPipeline completed. Check console trace output above for span details.");
}
catch (OperationCanceledException)
{
    pipelineActivity?.SetTag("pipeline.status", "cancelled");
    Console.WriteLine("\n[Cancelled]");
}

Console.WriteLine("\n=== Exercise complete! ===");

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
