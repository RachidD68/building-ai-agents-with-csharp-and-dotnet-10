// ============================================================================
// Exercise 03 — Multimodal Support Agent (Challenge)
// ----------------------------------------------------------------------------
// A support agent that reads a SCREENSHOT and answers a question about it. This
// is the real-world opener of Chapter 11 made runnable: the user pastes an error
// dialog and asks "what went wrong and how do I fix it?". The agent gets BOTH the
// image and the question in a single multimodal ChatMessage, and replies with a
// diagnosis grounded in what is actually on the screen.
//
// The included screenshot (assets/error-screenshot.png) shows a DB connection
// timeout dialog. Pass your own with a path argument:
//   dotnet run -- "C:\path\to\screenshot.png" "Why am I seeing this?"
//
// Needs a vision model under "VisionModel" (ollama llava / cloud gpt-5.6-terra). If none
// is configured, prints "[skipped]" and exits 0.
//
// STRETCH (left for the reader): swap the image for an audio file using the
// transcribe-then-prompt pattern from the chapter — transcribe with a STT service,
// then send the transcript as TextContent to the same agent.
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

Console.WriteLine("=== Exercise 03: Multimodal Support Agent ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

IChatClient? visionClient = BuildVisionClient(config);
if (visionClient is null)
{
    Console.WriteLine("[skipped: configure a vision model]");
    Console.WriteLine("Set \"VisionModel\" (ollama llava, or openai/azure gpt-5.6-terra). Local: ollama pull llava");
    return;
}

using IChatClient chatClient = visionClient;

// Image path + question: from CLI args, else the bundled screenshot + default question.
string imagePath = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "assets", "error-screenshot.png");

string question = args.Length > 1
    ? args[1]
    : "I got this error in our app. What went wrong, and what should I try first?";

if (!File.Exists(imagePath))
{
    Console.WriteLine($"[skipped] No screenshot found at: {imagePath}");
    return;
}

byte[] bytes = await File.ReadAllBytesAsync(imagePath, cts.Token);

ChatClientAgent agent = new(
    chatClient,
    instructions: """
        You are a technical support agent. The user shares a screenshot and a question.
        Read the screenshot carefully — error titles, codes, and button labels matter.
        Answer in two parts: (1) what the screen is telling the user, quoting any error
        code you can read, and (2) the first concrete troubleshooting step. Be specific
        and do not invent details that are not visible.
        """,
    name: "SupportAgent");

ChatMessage message = new(ChatRole.User,
[
    new TextContent(question),
    new DataContent(bytes, mediaType: "image/png"),
]);

try
{
    AgentSession session = await agent.CreateSessionAsync(cts.Token);
    AgentResponse response = await agent.RunAsync(message, session, cancellationToken: cts.Token);

    Console.WriteLine($"Screenshot : {imagePath} ({bytes.Length:N0} bytes)");
    Console.WriteLine($"User       : {question}\n");
    Console.WriteLine($"Agent      : {response.Text}\n");
    Console.WriteLine("=== Done ===");
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n[Cancelled]");
}
catch (Exception ex)
{
    Console.WriteLine($"\n[error] Vision call failed: {ex.Message}");
    Console.WriteLine("Confirm the model is a VISION model (llava / gpt-5.6-terra).");
}

static IChatClient? BuildVisionClient(IConfiguration config)
{
    string provider = (config["VisionModel:Provider"] ?? "ollama").ToLowerInvariant();
    switch (provider)
    {
        case "ollama":
        {
            string model = config["VisionModel:Ollama:Model"] ?? "llava";
            if (model.Contains("llava", StringComparison.OrdinalIgnoreCase)
                || model.Contains("vision", StringComparison.OrdinalIgnoreCase))
            {
                return new OllamaSharp.OllamaApiClient(
                    new Uri(config["VisionModel:Ollama:Endpoint"] ?? "http://localhost:11434"), model);
            }
            return null;
        }
        case "openai":
        {
            string? key = config["VisionModel:OpenAI:ApiKey"];
            return string.IsNullOrWhiteSpace(key) ? null
                : new OpenAI.OpenAIClient(key).GetChatClient(config["VisionModel:OpenAI:Model"] ?? "gpt-5.6-terra").AsIChatClient();
        }
        case "azure":
        {
            string? endpoint = config["VisionModel:AzureOpenAI:Endpoint"];
            return string.IsNullOrWhiteSpace(endpoint) ? null
                : new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential())
                    .GetChatClient(config["VisionModel:AzureOpenAI:Model"] ?? "gpt-5.6-terra").AsIChatClient();
        }
        default:
            return null;
    }
}
