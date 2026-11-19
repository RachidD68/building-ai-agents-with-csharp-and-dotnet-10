// ============================================================================
// Chapter 11 — Multimodal Agents (Vision Basics)
// ----------------------------------------------------------------------------
// The SAME agent and the SAME RunAsync you learned in Chapter 5 — only the
// ChatMessage is richer. Instead of a string, we build a message whose content
// is a list of AIContent parts: a TextContent (the question) plus an image part.
//
// We send an image TWO ways, both reaching the vision model unchanged:
//   1. HOSTED URL   -> UriContent(url, mediaType)          (model fetches it)
//   2. LOCAL FILE   -> DataContent(File.ReadAllBytes, ...) (bytes inlined)
//
// DOC GAP worth knowing: the official MAF multimodal page shows only UriContent.
// The local-file/bytes path is DataContent — and it is the one you need for
// images that never leave your machine. This sample proves both work.
//
// Vision needs a VISION model. Configure it under "VisionModel" in
// appsettings.json (separate from the chat "LlmProvider" so your default
// llama3.2 chat config is untouched):
//   - Ollama  : "llava" or "llama3.2-vision"   (local, offline)
//   - Cloud   : "gpt-5.6-terra" via openai/azure
// The default Ollama chat model llama3.2 is TEXT-ONLY: it will NOT accept images.
//
// If no vision model is reachable, the program prints a clear "[skipped]" note
// and exits 0 — the same graceful gate as the Chapter 16 Content Safety sample.
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

Console.WriteLine("=== Chapter 11: Vision Basics — one agent, a richer message ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// ---------------------------------------------------------------
// Step 1: Build a VISION-capable chat client (separate config section).
// ---------------------------------------------------------------
// Returns null when the configured provider/model is text-only or unconfigured,
// so we can print a friendly skip instead of throwing.
IChatClient? visionClient = BuildVisionClient(config);

if (visionClient is null)
{
    Console.WriteLine("[skipped: configure a vision model]");
    Console.WriteLine();
    Console.WriteLine("Set a VISION model under \"VisionModel\" in appsettings.json (or user secrets):");
    Console.WriteLine("  Provider = ollama   Model = llava   (or llama3.2-vision)  -> local, offline");
    Console.WriteLine("  Provider = openai   Model = gpt-5.6-terra   ApiKey = ...");
    Console.WriteLine("  Provider = azure    Model = gpt-5.6-terra   Endpoint = ...");
    Console.WriteLine("  Provider = github   Model = openai/gpt-5.6-terra   Token = ...");
    Console.WriteLine();
    Console.WriteLine("NOTE: the default Ollama chat model 'llama3.2' is TEXT-ONLY and will");
    Console.WriteLine("      reject images. Pull a vision model first:  ollama pull llava");
    return; // exit 0 — builds and runs offline without a vision model
}

using IChatClient chatClient = visionClient;

// ---------------------------------------------------------------
// Step 2: A vision agent is an ORDINARY agent. Nothing special here —
// we reuse the Chapter 5 constructor and the Chapter 5 RunAsync.
// ---------------------------------------------------------------
ChatClientAgent agent = new(
    chatClient,
    instructions: "You are a precise visual assistant. Describe images factually and "
                + "concisely. Report what you can actually see; do not speculate.",
    name: "VisionAssistant");

AgentSession session = await agent.CreateSessionAsync(cts.Token);

try
{
    // -----------------------------------------------------------
    // Way 1: HOSTED URL -> UriContent. The model fetches the bytes itself.
    // A stable public domain image (Wikimedia "example" test JPEG).
    // -----------------------------------------------------------
    Console.WriteLine("--- Way 1: hosted URL (UriContent) ---");

    const string imageUrl =
        "https://upload.wikimedia.org/wikipedia/commons/3/3a/Cat03.jpg";

    ChatMessage urlMessage = new(ChatRole.User,
    [
        new TextContent("What animal is in this image? Answer in one short sentence."),
        new UriContent(imageUrl, mediaType: "image/jpeg"),
    ]);

    AgentResponse urlResponse = await agent.RunAsync(urlMessage, session, cancellationToken: cts.Token);
    Console.WriteLine($"Image : {imageUrl}");
    Console.WriteLine($"Agent : {urlResponse.Text}\n");

    // -----------------------------------------------------------
    // Way 2: LOCAL FILE -> DataContent from bytes. Nothing leaves the box;
    // the raw bytes are inlined into the request. This is the committed
    // book cover (assets/cover-front.png).
    // -----------------------------------------------------------
    Console.WriteLine("--- Way 2: local file (DataContent from bytes) ---");

    string imagePath = Path.Combine(AppContext.BaseDirectory, "assets", "cover-front.png");
    if (!File.Exists(imagePath))
    {
        Console.WriteLine($"[skipped] local image not found at {imagePath}");
    }
    else
    {
        byte[] bytes = await File.ReadAllBytesAsync(imagePath, cts.Token);

        ChatMessage fileMessage = new(ChatRole.User,
        [
            new TextContent("Describe this book cover: its overall style, colors, and any text you can read."),
            new DataContent(bytes, mediaType: "image/png"),
        ]);

        AgentSession fileSession = await agent.CreateSessionAsync(cts.Token);
        AgentResponse fileResponse = await agent.RunAsync(fileMessage, fileSession, cancellationToken: cts.Token);
        Console.WriteLine($"Image : {imagePath} ({bytes.Length:N0} bytes inlined)");
        Console.WriteLine($"Agent : {fileResponse.Text}\n");
    }

    Console.WriteLine("=== Vision Basics demo complete! ===");
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n[Cancelled by user]");
}
catch (Exception ex)
{
    // A text-only model that slipped past detection will fail here. Be honest
    // about why rather than crashing with a raw stack trace.
    Console.WriteLine();
    Console.WriteLine($"[error] The vision call failed: {ex.Message}");
    Console.WriteLine("If you are on Ollama, confirm the model is a VISION model (llava /");
    Console.WriteLine("llama3.2-vision) and is pulled. The default 'llama3.2' is text-only.");
}

// ---------------------------------------------------------------
// BuildVisionClient — reads the "VisionModel" section and returns a chat client
// for a vision-capable model, or null when none is configured/usable.
// ---------------------------------------------------------------
static IChatClient? BuildVisionClient(IConfiguration config)
{
    string provider = (config["VisionModel:Provider"] ?? "ollama").ToLowerInvariant();

    switch (provider)
    {
        case "ollama":
        {
            string model = config["VisionModel:Ollama:Model"] ?? "llava";
            // Guard against the classic mistake: pointing vision at a text-only model.
            if (model.Contains("llava", StringComparison.OrdinalIgnoreCase)
                || model.Contains("vision", StringComparison.OrdinalIgnoreCase)
                || model.Contains("llama3.2-vision", StringComparison.OrdinalIgnoreCase))
            {
                string endpoint = config["VisionModel:Ollama:Endpoint"] ?? "http://localhost:11434";
                return new OllamaSharp.OllamaApiClient(new Uri(endpoint), model);
            }
            return null; // text-only model configured -> skip
        }

        case "openai":
        {
            string? key = config["VisionModel:OpenAI:ApiKey"];
            if (string.IsNullOrWhiteSpace(key)) return null;
            string model = config["VisionModel:OpenAI:Model"] ?? "gpt-5.6-terra";
            return new OpenAI.OpenAIClient(key).GetChatClient(model).AsIChatClient();
        }

        case "azure":
        {
            string? endpoint = config["VisionModel:AzureOpenAI:Endpoint"];
            if (string.IsNullOrWhiteSpace(endpoint)) return null;
            string model = config["VisionModel:AzureOpenAI:Model"] ?? "gpt-5.6-terra";
            return new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential())
                .GetChatClient(model)
                .AsIChatClient();
        }

        default:
            return null;
    }
}
