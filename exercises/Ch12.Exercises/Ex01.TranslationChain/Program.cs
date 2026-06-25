// ============================================================================
// Exercise 01 — Translation Chain (Beginner)
// Chain 3 agents: Translator → Summarizer → Formatter. FULLY STANDALONE.
// ============================================================================

using System.ClientModel;
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

// Define the three agents
ChatClientAgent translator = new(chatClient,
    instructions: "You are a translator. Translate the given text to English. Return ONLY the translation, nothing else.",
    name: "Translator");

ChatClientAgent summarizer = new(chatClient,
    instructions: "You are a summarizer. Summarize the given text into 2-3 concise sentences. Return ONLY the summary.",
    name: "Summarizer");

ChatClientAgent formatter = new(chatClient,
    instructions: "You are a formatter. Take the given text and format it as a bulleted list of key points. Use '- ' for each bullet. Return ONLY the bulleted list.",
    name: "Formatter");

// Sample input in French
string input = """
    L'intelligence artificielle transforme profondement le developpement logiciel moderne.
    Les grands modeles de langage permettent aux developpeurs de generer du code, d'analyser
    des bugs et d'automatiser des taches repetitives. Les frameworks comme .NET integrent
    desormais des capacites d'IA natives, rendant la creation d'agents intelligents accessible
    a tous les developpeurs. Cette revolution impacte la productivite, la qualite du code et
    les methodes de travail des equipes de developpement dans le monde entier.
    """;

Console.WriteLine("--- Input (French) ---");
Console.WriteLine(input.Trim());
Console.WriteLine();

try
{
    // Stage 1: Translate
    Console.WriteLine("--- Stage 1: Translation ---\n");
    AgentSession s1 = await translator.CreateSessionAsync(cts.Token);
    AgentResponse r1 = await translator.RunAsync(input, s1, cancellationToken: cts.Token);
    string translated = r1.Text ?? string.Empty;
    Console.WriteLine(translated);
    Console.WriteLine();

    // Stage 2: Summarize
    Console.WriteLine("--- Stage 2: Summary ---\n");
    AgentSession s2 = await summarizer.CreateSessionAsync(cts.Token);
    AgentResponse r2 = await summarizer.RunAsync(translated, s2, cancellationToken: cts.Token);
    string summarized = r2.Text ?? string.Empty;
    Console.WriteLine(summarized);
    Console.WriteLine();

    // Stage 3: Format
    Console.WriteLine("--- Stage 3: Formatted Output ---\n");
    AgentSession s3 = await formatter.CreateSessionAsync(cts.Token);
    AgentResponse r3 = await formatter.RunAsync(summarized, s3, cancellationToken: cts.Token);
    Console.WriteLine(r3.Text);
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n[Cancelled]");
}

Console.WriteLine("\n=== Exercise complete! ===");

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
