// ============================================================================
// Exercise 01 — Multi-Provider Chat (Beginner)
// Interactive console chat that lets the user pick a provider
// (azure/openai/github/ollama) and have a single-turn conversation.
// FULLY STANDALONE — no project references to Common.
// ============================================================================

using System.ClientModel;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

using IHost host = builder.Build();
IConfiguration config = host.Services.GetRequiredService<IConfiguration>();

Console.WriteLine("=== Exercise: Multi-Provider Chat ===\n");
Console.WriteLine("Available providers: azure, openai, github, ollama");
Console.Write("Select provider: ");

// Empty input (or EOF) falls back to the repo's offline default, ollama —
// never to a hosted provider that would then fail on a missing credential.
string? providerInput = Console.ReadLine()?.Trim().ToLowerInvariant();
string provider = string.IsNullOrEmpty(providerInput) ? "ollama" : providerInput;

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

IChatClient chatClient = CreateChatClient(provider, config);

Console.WriteLine($"\nConnected to: {provider}");
Console.WriteLine("Type your message (or 'quit' to exit):\n");

while (true)
{
    Console.Write("You: ");
    string? input = Console.ReadLine();

    if (string.IsNullOrWhiteSpace(input) ||
        input.Equals("quit", StringComparison.OrdinalIgnoreCase))
    {
        break;
    }

    try
    {
        ChatResponse response = await chatClient.GetResponseAsync(
            input,
            cancellationToken: cts.Token);

        Console.WriteLine($"Assistant: {response.Text}\n");

        if (response.Usage is { } usage)
        {
            Console.WriteLine($"  [Tokens: {usage.InputTokenCount ?? 0} in, " +
                              $"{usage.OutputTokenCount ?? 0} out, " +
                              $"{usage.TotalTokenCount ?? 0} total]\n");
        }
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("\n[Cancelled]");
        break;
    }
}

Console.WriteLine("=== Exercise complete! ===");

// ---------------------------------------------------------------
// Provider factory — fully standalone
// ---------------------------------------------------------------

/// <summary>
/// Creates an <see cref="IChatClient"/> for the specified provider.
/// </summary>
/// <param name="providerName">The provider name: azure, openai, github, or ollama.</param>
/// <param name="configuration">The configuration containing User Secrets.</param>
/// <returns>A configured <see cref="IChatClient"/>.</returns>
static IChatClient CreateChatClient(string providerName, IConfiguration configuration) =>
    providerName switch
    {
        "azure" => new AzureOpenAIClient(
                new Uri(configuration["LlmProvider:AzureOpenAI:Endpoint"]
                    ?? throw new InvalidOperationException("Missing LlmProvider:AzureOpenAI:Endpoint")),
                new DefaultAzureCredential())
            .GetChatClient(configuration["LlmProvider:AzureOpenAI:Model"] ?? "gpt-4o-mini")
            .AsIChatClient(),

        "openai" => new OpenAI.OpenAIClient(
                configuration["LlmProvider:OpenAI:ApiKey"] is { Length: > 0 } oaKey
                    ? oaKey
                    : throw new InvalidOperationException("Missing LlmProvider:OpenAI:ApiKey"))
            .GetChatClient(configuration["LlmProvider:OpenAI:Model"] ?? "gpt-4o-mini")
            .AsIChatClient(),

        // is { Length: > 0 } rejects the empty-string placeholder that ships in
        // appsettings.json — a bare null-check lets "" through to
        // ApiKeyCredential, which throws a far less helpful ArgumentException.
        "github" => new OpenAI.OpenAIClient(
                new ApiKeyCredential(
                    configuration["LlmProvider:GitHub:Token"] is { Length: > 0 } ghToken
                        ? ghToken
                        : throw new InvalidOperationException("Missing LlmProvider:GitHub:Token")),
                new OpenAI.OpenAIClientOptions
                {
                    Endpoint = new Uri(
                        configuration["LlmProvider:GitHub:Endpoint"]
                            ?? "https://models.github.ai/inference")
                })
            .GetChatClient(configuration["LlmProvider:GitHub:Model"] ?? "openai/gpt-4o-mini")
            .AsIChatClient(),

        "ollama" => new OllamaSharp.OllamaApiClient(
                new Uri(configuration["LlmProvider:Ollama:Endpoint"] ?? "http://localhost:11434"),
                configuration["LlmProvider:Ollama:Model"] ?? "llama3.2"),

        _ => throw new InvalidOperationException(
            $"Unknown provider: '{providerName}'. Valid: azure, openai, github, ollama.")
    };
