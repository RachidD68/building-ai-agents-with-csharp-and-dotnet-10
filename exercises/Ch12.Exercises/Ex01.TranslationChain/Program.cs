// ============================================================================
// Exercise 01 — Translation Chain (Beginner)
// Three agents in sequence: Translator (FR → EN) → Formalizer → Summarizer,
// instrumented with Stopwatch and benchmarked against a single-agent
// baseline that does all three jobs in one prompt. FULLY STANDALONE.
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

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

using IHost host = builder.Build();
IConfiguration config = host.Services.GetRequiredService<IConfiguration>();
IChatClient chatClient = CreateChatClient(config);

Console.WriteLine("=== Exercise: Translation Chain ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// ---------------------------------------------------------------
// The three pipeline agents: Translator → Formalizer → Summarizer
// ---------------------------------------------------------------
ChatClientAgent translator = new(chatClient,
    instructions: "You are a professional French-to-English translator. Translate the given French text into fluent, natural English. Return ONLY the translation, nothing else.",
    name: "Translator");

ChatClientAgent formalizer = new(chatClient,
    instructions: "Rewrite this English text in formal business tone, preserving meaning. Return ONLY the rewritten text, nothing else.",
    name: "Formalizer");

ChatClientAgent summarizer = new(chatClient,
    instructions: "You are a summarizer. Condense the given text into 2-3 concise sentences. Return ONLY the summary, nothing else.",
    name: "Summarizer");

// Sample French memo to push through the pipeline
string frenchMemo = """
    Objet : migration vers .NET 10

    Salut l'équipe, petit rappel : on bascule les services de paiement sur
    .NET 10 à partir de lundi prochain. Pensez à mettre à jour vos pipelines
    de build et à vérifier vos packages NuGet avant la bascule. En cas de
    souci, postez sur le canal #migration — l'astreinte répondra en priorité.
    Merci de bloquer une heure pour les tests de non-régression.
    """;

Console.WriteLine("--- Input (French memo) ---");
Console.WriteLine(frenchMemo.Trim());
Console.WriteLine();

try
{
    // ---------------------------------------------------------------
    // Run A — the three-stage pipeline, one agent per job
    // ---------------------------------------------------------------
    Console.WriteLine("--- Run A: Three-agent pipeline ---\n");

    Stopwatch pipelineWatch = Stopwatch.StartNew();

    (string english, long translateMs) = await RunStageAsync(translator, frenchMemo);
    Console.WriteLine($"[Stage 1 — Translator, {translateMs} ms]");
    Console.WriteLine(english);
    Console.WriteLine();

    (string formal, long formalizeMs) = await RunStageAsync(formalizer, english);
    Console.WriteLine($"[Stage 2 — Formalizer, {formalizeMs} ms]");
    Console.WriteLine(formal);
    Console.WriteLine();

    (string pipelineSummary, long summarizeMs) = await RunStageAsync(summarizer, formal);
    Console.WriteLine($"[Stage 3 — Summarizer, {summarizeMs} ms]");
    Console.WriteLine(pipelineSummary);
    Console.WriteLine();

    pipelineWatch.Stop();

    // ---------------------------------------------------------------
    // Run B — single-agent baseline: all three jobs in one prompt
    // ---------------------------------------------------------------
    Console.WriteLine("--- Run B: Single-agent baseline ---\n");

    ChatClientAgent allInOne = new(chatClient,
        instructions: """
            You are a translation assistant. Given a French text, do all of the
            following in one pass: translate it to English, rewrite the
            translation in formal business tone, then summarize the result in
            2-3 concise sentences. Return ONLY the final summary, nothing else.
            """,
        name: "AllInOne");

    (string baselineSummary, long baselineMs) = await RunStageAsync(allInOne, frenchMemo);
    Console.WriteLine($"[AllInOne — {baselineMs} ms]");
    Console.WriteLine(baselineSummary);
    Console.WriteLine();

    // ---------------------------------------------------------------
    // Comparison — latency measured, quality eyeballed
    // ---------------------------------------------------------------
    Console.WriteLine("--- Comparison ---\n");
    Console.WriteLine($"  Pipeline : {pipelineWatch.ElapsedMilliseconds,6} ms " +
                      $"(translate {translateMs} + formalize {formalizeMs} + summarize {summarizeMs}), " +
                      $"summary {WordCount(pipelineSummary)} words");
    Console.WriteLine($"  Baseline : {baselineMs,6} ms (one prompt), " +
                      $"summary {WordCount(baselineSummary)} words");
    Console.WriteLine();
    Console.WriteLine("  The pipeline pays one model round-trip per stage; the single agent");
    Console.WriteLine("  pays one round-trip total. Compare the two summaries above for");
    Console.WriteLine("  quality — tone, faithfulness, and detail are where splitting the");
    Console.WriteLine("  job into single-purpose agents earns (or fails to earn) its cost.");
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n[Cancelled]");
}

Console.WriteLine("\n=== Exercise complete! ===");

// ---------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------

/// <summary>Runs one agent over the input and reports elapsed milliseconds.</summary>
async Task<(string Text, long Ms)> RunStageAsync(ChatClientAgent agent, string input)
{
    Stopwatch sw = Stopwatch.StartNew();
    AgentSession session = await agent.CreateSessionAsync(cts.Token);
    AgentResponse response = await agent.RunAsync(input, session, cancellationToken: cts.Token);
    sw.Stop();
    return (response.Text ?? string.Empty, sw.ElapsedMilliseconds);
}

/// <summary>Counts whitespace-separated words in a text.</summary>
static int WordCount(string text) =>
    text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

// ---------------------------------------------------------------
// Provider factory
// ---------------------------------------------------------------

/// <summary>Creates an IChatClient from configuration.</summary>
static IChatClient CreateChatClient(IConfiguration configuration)
{
    string provider = configuration["LlmProvider:Provider"]?.ToLowerInvariant() ?? "github";
    return provider switch
    {
        "azure" => new AzureOpenAIClient(new Uri(configuration["LlmProvider:AzureOpenAI:Endpoint"] ?? throw new InvalidOperationException("Missing endpoint")), new DefaultAzureCredential())
            .GetChatClient(configuration["LlmProvider:AzureOpenAI:Model"] ?? "gpt-4o-mini").AsIChatClient(),
        "openai" => new OpenAI.OpenAIClient(configuration["LlmProvider:OpenAI:ApiKey"] ?? throw new InvalidOperationException("Missing key"))
            .GetChatClient(configuration["LlmProvider:OpenAI:Model"] ?? "gpt-4o-mini").AsIChatClient(),
        "github" => new OpenAI.OpenAIClient(new ApiKeyCredential(configuration["LlmProvider:GitHub:Token"] ?? throw new InvalidOperationException("Missing token")),
                new OpenAI.OpenAIClientOptions { Endpoint = new Uri(configuration["LlmProvider:GitHub:Endpoint"] ?? "https://models.github.ai/inference") })
            .GetChatClient(configuration["LlmProvider:GitHub:Model"] ?? "openai/gpt-4o-mini").AsIChatClient(),
        "ollama" => new OllamaSharp.OllamaApiClient(new Uri(configuration["LlmProvider:Ollama:Endpoint"] ?? "http://localhost:11434"), configuration["LlmProvider:Ollama:Model"] ?? "llama3.2"),
        _ => throw new InvalidOperationException($"Unknown provider: '{provider}'")
    };
}
