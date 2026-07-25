// ============================================================================
// Exercise 01 — Describe an Image: DataContent vs UriContent (Basic)
// ----------------------------------------------------------------------------
// Two runs of the SAME image question through a vision agent, timed:
//   Run 1 — LOCAL BYTES  -> DataContent(File.ReadAllBytes, mediaType)
//   Run 2 — HOSTED URL   -> UriContent(url, mediaType)  (the model fetches it)
// Then the outputs are compared side by side. The lesson is in the difference:
// DataContent inlines the bytes into the request and works everywhere;
// UriContent requires a provider that fetches URLs server-side (cloud models
// do; local Ollama models do NOT), so the URL run may be skipped.
//
// Usage:  dotnet run -- "C:\path\to\photo.jpg"
//         (with no argument it looks for sample.png next to the executable)
//
// Needs a vision model under "VisionModel" (ollama llava / cloud gpt-4o). If none
// is configured, prints a clear "[skipped]" note and exits 0.
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

Console.WriteLine("=== Exercise 01: Describe an Image — DataContent vs UriContent ===\n");

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

ChatClientAgent agent = new(
    chatClient,
    instructions: "You are a precise visual assistant. Describe what you actually see in the "
                + "image: subjects, setting, colors, and any readable text. Be concise.",
    name: "ImageDescriber");

// The SAME question goes through both content paths.
const string question = "Describe this image in two or three sentences.";

string? localAnswer = null;
TimeSpan localElapsed = TimeSpan.Zero;
string? urlAnswer = null;
TimeSpan urlElapsed = TimeSpan.Zero;

// ---------------------------------------------------------------
// Run 1 — LOCAL BYTES via DataContent
// ---------------------------------------------------------------
Console.WriteLine("--- Run 1: local bytes (DataContent) ---");

// Resolve the image path: first CLI arg, else sample.png beside the exe.
string imagePath = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "sample.png");

if (!File.Exists(imagePath))
{
    Console.WriteLine($"[skipped] No local image found at: {imagePath}");
    Console.WriteLine("Pass a path:  dotnet run -- \"C:\\path\\to\\image.png\"\n");
}
else
{
    string mediaType = MediaTypeFor(imagePath);
    byte[] bytes = await File.ReadAllBytesAsync(imagePath, cts.Token);

    ChatMessage dataMessage = new(ChatRole.User,
    [
        new TextContent(question),
        new DataContent(bytes, mediaType),
    ]);

    try
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        AgentSession session = await agent.CreateSessionAsync(cts.Token);
        AgentResponse response = await agent.RunAsync(dataMessage, session, cancellationToken: cts.Token);
        stopwatch.Stop();

        localAnswer = response.Text;
        localElapsed = stopwatch.Elapsed;

        Console.WriteLine($"Image : {imagePath} ({bytes.Length:N0} bytes, {mediaType})");
        Console.WriteLine($"Time  : {localElapsed.TotalSeconds:F1}s");
        Console.WriteLine($"Agent : {localAnswer}\n");
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("\n[Cancelled]");
        return;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[error] Local-bytes vision call failed: {ex.Message}");
        Console.WriteLine("Confirm the model is a VISION model (llava / gpt-4o), not text-only llama3.2.\n");
    }
}

// ---------------------------------------------------------------
// Run 2 — HOSTED URL via UriContent
// The model fetches the bytes itself, so this path needs a provider that
// fetches URLs server-side. Cloud models (gpt-4o) do; local Ollama models
// do NOT — on Ollama this run typically fails and is reported as skipped.
// A stable public-domain image (Wikimedia "example" test JPEG).
// ---------------------------------------------------------------
Console.WriteLine("--- Run 2: hosted URL (UriContent) ---");

const string imageUrl =
    "https://upload.wikimedia.org/wikipedia/commons/3/3a/Cat03.jpg";

ChatMessage urlMessage = new(ChatRole.User,
[
    new TextContent(question),
    new UriContent(imageUrl, mediaType: "image/jpeg"),
]);

try
{
    Stopwatch stopwatch = Stopwatch.StartNew();
    AgentSession session = await agent.CreateSessionAsync(cts.Token);
    AgentResponse response = await agent.RunAsync(urlMessage, session, cancellationToken: cts.Token);
    stopwatch.Stop();

    urlAnswer = response.Text;
    urlElapsed = stopwatch.Elapsed;

    Console.WriteLine($"Image : {imageUrl}");
    Console.WriteLine($"Time  : {urlElapsed.TotalSeconds:F1}s");
    Console.WriteLine($"Agent : {urlAnswer}\n");
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n[Cancelled]");
    return;
}
catch (Exception ex)
{
    Console.WriteLine($"[skipped] UriContent run failed on this provider: {ex.Message}");
    Console.WriteLine("URL images require a provider that fetches URLs (cloud gpt-4o does;");
    Console.WriteLine("local Ollama does not). Switch VisionModel to a cloud provider to see this path.\n");
}

// ---------------------------------------------------------------
// Side-by-side comparison — what each path returned.
// ---------------------------------------------------------------
Console.WriteLine("--- Comparison ---");
Console.WriteLine($"DataContent (local bytes) : {(localAnswer is null ? "[skipped]" : $"{localElapsed.TotalSeconds,5:F1}s | {localAnswer}")}");
Console.WriteLine($"UriContent  (hosted URL)  : {(urlAnswer is null ? "[skipped]" : $"{urlElapsed.TotalSeconds,5:F1}s | {urlAnswer}")}");

if (localAnswer is not null && urlAnswer is not null)
{
    Console.WriteLine("\nBoth paths reached the same model — DataContent inlined the bytes into the");
    Console.WriteLine("request; UriContent made the model fetch the image itself.");
}

Console.WriteLine("\n=== Done ===");

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
