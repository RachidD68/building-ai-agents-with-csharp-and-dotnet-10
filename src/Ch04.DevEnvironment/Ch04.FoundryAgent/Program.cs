// ============================================================================
// Chapter 4 — Microsoft Foundry: the 5th provider
// ----------------------------------------------------------------------------
// Microsoft Foundry is Microsoft's recommended path for new MAF projects. The
// canonical pattern is to build an AIProjectClient against your
// Azure AI Projects endpoint, then call .AsAIAgent(...) to get an AIAgent back.
// This bypasses the IChatClient layer entirely — useful when you want hosted
// agent features (Hosted-Files RAG, server-side tool execution, etc.).
//
// To run: configure LlmProvider:Foundry:ProjectEndpoint in appsettings.json
// (or set it via environment variable LlmProvider__Foundry__ProjectEndpoint).
// You must be signed in via `az login` for DefaultAzureCredential to work.
// ============================================================================

using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using AgenticAI.Common.Configuration;
using AgenticAI.Common.Extensions;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
IConfiguration config = builder.Configuration;

LlmSettings settings = new();
config.GetSection("LlmProvider").Bind(settings);

if (string.IsNullOrWhiteSpace(settings.Foundry.ProjectEndpoint))
{
    Console.WriteLine("Configure LlmProvider:Foundry:ProjectEndpoint in appsettings.json.");
    Console.WriteLine("Example: https://my-foundry.services.ai.azure.com/api/projects/my-project");
    Console.WriteLine("Then run `az login` so DefaultAzureCredential can authenticate.");
    return;
}

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine("=== Chapter 4: Microsoft Foundry agent ===");
Console.WriteLine($"Project endpoint: {settings.Foundry.ProjectEndpoint}");
Console.WriteLine($"Model:            {settings.Foundry.Model}");
Console.WriteLine();

// ── The canonical Foundry pattern: AIProjectClient → AsAIAgent → AIAgent ────────
AIAgent agent = new AIProjectClient(
        new Uri(settings.Foundry.ProjectEndpoint),
        new DefaultAzureCredential())
    .AsAIAgent(
        model: settings.Foundry.Model,
        instructions: "You are a friendly assistant. Keep your answers brief.",
        name: "HelloFoundry");

Console.WriteLine("--- Non-streaming response ---");
Console.WriteLine(await agent.RunAsync(
    "What is the largest city in France?",
    cancellationToken: cts.Token));
Console.WriteLine();

Console.WriteLine("--- Streaming response ---");
await foreach (AgentResponseUpdate update in agent.RunStreamingAsync(
    "Tell me one sentence about the .NET 10 LTS release.",
    cancellationToken: cts.Token))
{
    if (update.Text is { Length: > 0 } text)
    {
        Console.Write(text);
    }
}
Console.WriteLine();
Console.WriteLine("=== Done ===");
