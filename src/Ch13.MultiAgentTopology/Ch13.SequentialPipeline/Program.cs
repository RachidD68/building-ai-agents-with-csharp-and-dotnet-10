// ============================================================================
// Chapter 12 — Multi-Agent Systems (Sequential Pipeline)
// Demonstrates the simplest multi-agent pattern: two agents in sequence.
// Agent 1 (Translator) translates French text to English.
// Agent 2 (Summarizer) summarizes the English translation.
//
// Key concepts:
//   - Creating multiple ChatClientAgent instances with distinct personas
//   - Manual sequential chaining: output of agent 1 feeds into agent 2
//   - Each agent maintains its own session and instructions
//
// FORWARD REFERENCE: Chapter 12 introduces the Workflow engine that can
// automate this chaining with AgentWorkflowBuilder.BuildSequential().
// ============================================================================

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgenticAI.Common.Extensions;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);

using IHost host = builder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();

Console.WriteLine("=== Chapter 12: Sequential Pipeline — Translator → Summarizer ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// ---------------------------------------------------------------
// Step 1: Define the two agents with distinct personas
// ---------------------------------------------------------------
ChatClientAgent translatorAgent = new(
    chatClient,
    instructions: """
        You are a professional French-to-English translator.
        Translate the provided French text into fluent, natural English.
        Preserve the meaning, tone, and nuance of the original.
        Output ONLY the English translation, nothing else.
        """,
    name: "Translator",
    description: "Translates French text into English");

ChatClientAgent summarizerAgent = new(
    chatClient,
    instructions: """
        You are a concise summarizer.
        Take the provided English text and produce a brief summary
        (2-3 sentences maximum) that captures the key points.
        Output ONLY the summary, nothing else.
        """,
    name: "Summarizer",
    description: "Summarizes English text into 2-3 sentences");

// ---------------------------------------------------------------
// Step 2: French text to process through the pipeline
// ---------------------------------------------------------------
string frenchText = """
    La technologie de l'intelligence artificielle a connu des avancées
    remarquables au cours des dernières années. Les modèles de langage
    de grande taille sont désormais capables de comprendre et de générer
    du texte avec une précision impressionnante. Cette évolution ouvre
    de nouvelles possibilités dans de nombreux domaines, notamment la
    médecine, l'éducation et la recherche scientifique. Cependant, il
    est essentiel de développer ces technologies de manière responsable,
    en tenant compte des implications éthiques et sociales.
    """;

Console.WriteLine("[INPUT — French Text]");
Console.WriteLine(frenchText);

// ---------------------------------------------------------------
// Step 3: Stage 1 — Translation
// ---------------------------------------------------------------
Console.WriteLine("--- Stage 1: Translation (French → English) ---\n");

AgentSession translatorSession = await translatorAgent.CreateSessionAsync(cts.Token);

AgentResponse translationResponse = await translatorAgent.RunAsync(
    frenchText,
    translatorSession,
    cancellationToken: cts.Token);

string englishTranslation = translationResponse.Text;
Console.WriteLine("[Translator Output]");
Console.WriteLine(englishTranslation);
Console.WriteLine();

// ---------------------------------------------------------------
// Step 4: Stage 2 — Summarization
// ---------------------------------------------------------------
Console.WriteLine("--- Stage 2: Summarization ---\n");

AgentSession summarizerSession = await summarizerAgent.CreateSessionAsync(cts.Token);

AgentResponse summaryResponse = await summarizerAgent.RunAsync(
    englishTranslation,
    summarizerSession,
    cancellationToken: cts.Token);

Console.WriteLine("[Summarizer Output]");
Console.WriteLine(summaryResponse.Text);
Console.WriteLine();

Console.WriteLine("=== Sequential Pipeline complete! ===");
