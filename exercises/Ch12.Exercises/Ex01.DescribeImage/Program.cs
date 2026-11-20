// ============================================================================
// Exercise 01 — Describe an Image: DataContent vs UriContent (Basic)
// ----------------------------------------------------------------------------
// Two runs of the SAME question through a vision agent, timed:
//   Run 1 — LOCAL BYTES  -> DataContent(File.ReadAllBytes, mediaType)
//   Run 2 — HOSTED URL   -> UriContent(url, mediaType)
// Then the outputs are compared side by side. The lesson is in the difference:
// DataContent inlines the bytes and works on every vision provider. UriContent
// needs a provider that fetches the URL. The Ollama connector (OllamaSharp
// 5.4.25) drops the UriContent part WITHOUT an error, so on a local model the
// URL run still answers, but without the image. The program says so instead of
// printing that answer as a description.
//
// Usage:  dotnet run -- [image path] [image URL]
//         (defaults: the clean taxi receipt from the corpus, and a public test JPEG)
//         dotnet run -- --self-test   offline: what each run puts on the wire; RESULT n/n
//
// Needs a vision model under "VisionModel" (ollama llava / cloud gpt-5.6-terra). If none
// is configured, prints a clear "[skipped]" note and exits 0.
// ============================================================================

using System.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

const string Question = "Describe this image in two or three sentences.";
const string DefaultUrl = "https://upload.wikimedia.org/wikipedia/commons/3/3a/Cat03.jpg";

if (args.Contains("--self-test")) return await SelfTest.RunAsync(Question);

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
    Console.WriteLine("Set \"VisionModel\" in appsettings.json (ollama llava, or openai gpt-5.6-terra).");
    return 0;
}

using IChatClient chatClient = visionClient;
ChatClientAgent agent = Describer.Create(chatClient);

string imagePath = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "sample.png");
string imageUrl = args.Length > 1 ? args[1] : DefaultUrl;

(string? Text, TimeSpan Elapsed)? local = null, remote = null;
try
{
    Console.WriteLine("--- Run 1: local bytes (DataContent) ---");
    if (!File.Exists(imagePath))
    {
        Console.WriteLine($"[skipped] No local image found at: {imagePath}\n");
    }
    else
    {
        byte[] bytes = await File.ReadAllBytesAsync(imagePath, cts.Token);
        local = await TimedRun(agent, Describer.BytesMessage(Question, bytes, MediaTypeFor(imagePath)), cts.Token);
        Console.WriteLine($"Image : {imagePath} ({bytes.Length:N0} bytes)");
        Console.WriteLine($"Time  : {local.Value.Elapsed.TotalSeconds:F1}s");
        Console.WriteLine($"Agent : {local.Value.Text}\n");
    }

    Console.WriteLine("--- Run 2: hosted URL (UriContent) ---");
    remote = await TimedRun(agent, Describer.UrlMessage(Question, imageUrl), cts.Token);
    Console.WriteLine($"Image : {imageUrl}");
    Console.WriteLine($"Time  : {remote.Value.Elapsed.TotalSeconds:F1}s");
    Console.WriteLine($"Agent : {remote.Value.Text}");
    Console.WriteLine($"Note  : {Describer.UrlNote(chatClient)}\n");
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n[Cancelled]");
    return 0;
}
catch (Exception ex)
{
    Console.WriteLine($"[error] The vision call failed: {ex.Message}");
    Console.WriteLine("Confirm the model is a VISION model (llava / gpt-5.6-terra), not text-only llama3.2.\n");
}

Console.WriteLine("--- Comparison ---");
Console.WriteLine($"DataContent (local bytes) : {Show(local)}");
Console.WriteLine($"UriContent  (hosted URL)  : {Show(remote)}");
Console.WriteLine("\n=== Done ===");
return 0;

static async Task<(string? Text, TimeSpan Elapsed)> TimedRun(ChatClientAgent agent, ChatMessage message, CancellationToken ct)
{
    Stopwatch stopwatch = Stopwatch.StartNew();
    AgentSession session = await agent.CreateSessionAsync(ct);
    AgentResponse response = await agent.RunAsync(message, session, cancellationToken: ct);
    return (response.Text, stopwatch.Elapsed);
}

static string Show((string? Text, TimeSpan Elapsed)? run) =>
    run is { } r ? $"{r.Elapsed.TotalSeconds,5:F1}s | {r.Text}" : "[skipped]";

static string MediaTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
{
    ".jpg" or ".jpeg" => "image/jpeg",
    ".gif" => "image/gif",
    ".webp" => "image/webp",
    _ => "image/png",
};

static IChatClient? BuildVisionClient(IConfiguration config)
{
    string provider = (config["VisionModel:Provider"] ?? "ollama").ToLowerInvariant();
    if (provider == "ollama")
    {
        string model = config["VisionModel:Ollama:Model"] ?? "llava";
        bool vision = model.Contains("llava", StringComparison.OrdinalIgnoreCase)
                   || model.Contains("vision", StringComparison.OrdinalIgnoreCase);
        return vision
            ? new OllamaSharp.OllamaApiClient(new Uri(config["VisionModel:Ollama:Endpoint"] ?? "http://localhost:11434"), model)
            : null;
    }
    if (provider == "openai" && config["VisionModel:OpenAI:ApiKey"] is { Length: > 0 } key)
        return new OpenAI.OpenAIClient(key).GetChatClient(config["VisionModel:OpenAI:Model"] ?? "gpt-5.6-terra").AsIChatClient();
    return null;
}

/// <summary>The agent and the two messages, shared by the live run and the self-test.</summary>
internal static class Describer
{
    public static ChatClientAgent Create(IChatClient client) => new(
        client,
        instructions: "You are a precise visual assistant. Describe what you actually see in the "
                    + "image: subjects, setting, colors, and any readable text. Be concise. "
                    + "If no image reached you, say so.",
        name: "ImageDescriber");

    public static ChatMessage BytesMessage(string question, byte[] bytes, string mediaType) =>
        new(ChatRole.User, [new TextContent(question), new DataContent(bytes, mediaType)]);

    public static ChatMessage UrlMessage(string question, string url) =>
        new(ChatRole.User, [new TextContent(question), new UriContent(url, "image/jpeg")]);

    /// <summary>What happened to the URL, by connector: explained, not hidden.</summary>
    public static string UrlNote(IChatClient client) => client is OllamaSharp.OllamaApiClient
        ? "the Ollama connector dropped the URL part; this answer was written without the image."
        : "the URL was passed to the provider, which must fetch it itself.";
}

/// <summary>
/// Offline check of the exercise: both runs go through the real Ollama connector
/// into a recording handler (no server), and the request bodies are inspected.
/// </summary>
internal static class SelfTest
{
    public static async Task<int> RunAsync(string question)
    {
        Console.WriteLine("=== Exercise 01: DataContent vs UriContent (self-test) ===");
        int passed = 0, total = 0;
        void Expect(string name, bool ok) { total++; if (ok) passed++; Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}"); }

        RecordingHandler wire = new();
        using HttpClient http = new(wire) { BaseAddress = new Uri("http://localhost:11434") };
        using IChatClient ollama = new OllamaSharp.OllamaApiClient(http, "llava");
        ChatClientAgent agent = Describer.Create(ollama);

        await agent.RunAsync(Describer.BytesMessage(question, [1, 2, 3], "image/png"), await agent.CreateSessionAsync());
        Expect("run 1 sends the bytes inline (\"images\":[\"AQID\"])", wire.Body.Contains("\"images\":[\"AQID\"]"));

        await agent.RunAsync(Describer.UrlMessage(question, "https://example.com/same-image.jpg"), await agent.CreateSessionAsync());
        Expect("run 2 sends the question but neither an image nor the URL",
            wire.Body.Contains("Describe this image") && !wire.Body.Contains("images") && !wire.Body.Contains("example.com"));
        Expect("the URL run's answer is labelled as written without the image",
            Describer.UrlNote(ollama).Contains("without the image"));

        Console.WriteLine($"\nRESULT {passed}/{total}");
        return passed == total ? 0 : 1;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string Body { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            const string reply = """{"model":"llava","created_at":"2026-11-20T00:00:00Z","message":{"role":"assistant","content":"ok"},"done":true,"done_reason":"stop"}""";
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(reply + "\n", System.Text.Encoding.UTF8, "application/x-ndjson"),
            };
        }
    }
}
