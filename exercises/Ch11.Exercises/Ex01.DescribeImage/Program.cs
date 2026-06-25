// ============================================================================
// Exercise 01 — Describe a Local Image (Basic)
// ----------------------------------------------------------------------------
// Point the app at any local image file (a path argument, or a default), build
// a multimodal ChatMessage from its BYTES (DataContent), and let a vision agent
// describe it. This is the local-file half of Ch11.VisionBasics, standalone.
//
// Usage:  dotnet run -- "C:\path\to\photo.jpg"
//         (with no argument it looks for sample.png next to the executable)
//
// Needs a vision model under "VisionModel" (ollama llava / cloud gpt-4o). If none
// is configured, prints a clear "[skipped]" note and exits 0.
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

Console.WriteLine("=== Exercise 01: Describe a Local Image ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

IChatClient? visionClient = BuildVisionClient(config);
if (visionClient is null)
{
    Console.WriteLine("[skipped: configure a vision model]");
    Console.WriteLine("Set \"VisionModel\" in appsettings.json (ollama llava, or openai/azure/github gpt-4o).");
    Console.WriteLine("Local vision: ollama pull llava");
    return;
}

using IChatClient chatClient = visionClient;

// Resolve the image path: first CLI arg, else sample.png beside the exe.
string imagePath = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "sample.png");

if (!File.Exists(imagePath))
{
    Console.WriteLine($"[skipped] No image found at: {imagePath}");
    Console.WriteLine("Pass a path:  dotnet run -- \"C:\\path\\to\\image.png\"");
    return;
}

string mediaType = MediaTypeFor(imagePath);
byte[] bytes = await File.ReadAllBytesAsync(imagePath, cts.Token);

ChatClientAgent agent = new(
    chatClient,
    instructions: "You are a precise visual assistant. Describe what you actually see in the "
                + "image: subjects, setting, colors, and any readable text. Be concise.",
    name: "ImageDescriber");

ChatMessage message = new(ChatRole.User,
[
    new TextContent("Describe this image in two or three sentences."),
    new DataContent(bytes, mediaType),
]);

try
{
    AgentSession session = await agent.CreateSessionAsync(cts.Token);
    AgentResponse response = await agent.RunAsync(message, session, cancellationToken: cts.Token);

    Console.WriteLine($"Image : {imagePath} ({bytes.Length:N0} bytes, {mediaType})");
    Console.WriteLine($"Agent : {response.Text}\n");
    Console.WriteLine("=== Done ===");
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n[Cancelled]");
}
catch (Exception ex)
{
    Console.WriteLine($"\n[error] Vision call failed: {ex.Message}");
    Console.WriteLine("Confirm the model is a VISION model (llava / gpt-4o), not text-only llama3.2.");
}

static string MediaTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
{
    ".png" => "image/png",
    ".jpg" or ".jpeg" => "image/jpeg",
    ".gif" => "image/gif",
    ".webp" => "image/webp",
    ".bmp" => "image/bmp",
    _ => "image/png",
};

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
                : new OpenAI.OpenAIClient(key).GetChatClient(config["VisionModel:OpenAI:Model"] ?? "gpt-4o").AsIChatClient();
        }
        case "azure":
        {
            string? endpoint = config["VisionModel:AzureOpenAI:Endpoint"];
            return string.IsNullOrWhiteSpace(endpoint) ? null
                : new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential())
                    .GetChatClient(config["VisionModel:AzureOpenAI:Model"] ?? "gpt-4o").AsIChatClient();
        }
        case "github":
        {
            string? token = config["VisionModel:GitHub:Token"];
            return string.IsNullOrWhiteSpace(token) ? null
                : new OpenAI.OpenAIClient(
                        new ApiKeyCredential(token),
                        new OpenAI.OpenAIClientOptions { Endpoint = new Uri(config["VisionModel:GitHub:Endpoint"] ?? "https://models.github.ai/inference") })
                    .GetChatClient(config["VisionModel:GitHub:Model"] ?? "openai/gpt-4o").AsIChatClient();
        }
        default:
            return null;
    }
}
