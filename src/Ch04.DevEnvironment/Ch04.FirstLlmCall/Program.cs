// ============================================================================
// Chapter 4 — Your First LLM Call
// The reader's first real conversation with an AI model using
// Microsoft.Extensions.AI and the multi-provider pattern from AgenticAI.Common.
// ============================================================================

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgenticAI.Common.Extensions;

// ---------------------------------------------------------------
// Step 1: Build the host with configuration and DI
// ---------------------------------------------------------------
HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);


// Register the IChatClient using the multi-provider factory from Common.
// This reads the "LlmProvider" section from appsettings.json to decide which
// provider to use (azure, openai, or ollama).
builder.Services.AddLlmChatClient(builder.Configuration);

using IHost host = builder.Build();

// Resolve the configured chat client
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();

Console.WriteLine("=== Chapter 4: Your First LLM Call ===");
Console.WriteLine($"Provider configured. Chat client type: {chatClient.GetType().Name}\n");

using CancellationTokenSource cts = new();

// Allow Ctrl+C to cancel gracefully
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

// ---------------------------------------------------------------
// Step 2: Send a simple prompt and get a complete response
// ---------------------------------------------------------------
Console.WriteLine("--- Complete Response ---\n");

string prompt = "What are three interesting facts about the .NET programming language?";
Console.WriteLine($"Prompt: {prompt}\n");

try
{
    ChatResponse response = await chatClient.GetResponseAsync(prompt, cancellationToken: cts.Token);

    Console.WriteLine("Response:");
    Console.WriteLine(new string('-', 60));
    Console.WriteLine(response.Text);
    Console.WriteLine(new string('-', 60));

    // Display usage information if available
    if (response.Usage is { } usage)
    {
        Console.WriteLine($"\nUsage: {usage.InputTokenCount ?? 0} input tokens, " +
                          $"{usage.OutputTokenCount ?? 0} output tokens, " +
                          $"{usage.TotalTokenCount ?? 0} total");
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n[Cancelled by user]");
    return;
}
catch (Exception ex)
{
    Console.WriteLine($"\nError: {ex.Message}");
    Console.WriteLine("Make sure you have configured your API keys. See README.md for setup instructions.");
    return;
}

// ---------------------------------------------------------------
// Step 3: Streaming — watch tokens arrive in real-time
// ---------------------------------------------------------------
Console.WriteLine("\n--- Streaming Response ---\n");

string streamingPrompt = "Write a haiku about coding in C#.";
Console.WriteLine($"Prompt: {streamingPrompt}\n");
Console.Write("Response: ");

try
{
    await foreach (ChatResponseUpdate update in chatClient.GetStreamingResponseAsync(
        streamingPrompt,cancellationToken: cts.Token))
    {
        if (update.Text is { } text)
        {
            Console.Write(text);
        }
    }

    Console.WriteLine("\n");
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n[Cancelled by user]");
    return;
}
catch (Exception ex)
{
    Console.WriteLine($"\nStreaming error: {ex.Message}");
}

Console.WriteLine("=== First LLM call complete! ===");
