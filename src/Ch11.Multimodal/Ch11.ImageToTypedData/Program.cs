// ============================================================================
// Chapter 11 — Multimodal Agents (FLAGSHIP: Image -> typed C# record)
// ----------------------------------------------------------------------------
// This is the payoff of the chapter. We take an INVOICE IMAGE and get back a
// strongly-typed Invoice record — no OCR library, no regex, no parsing code.
// The vision model reads the picture; structured output (Chapter 7) maps the
// result onto our C# types.
//
// Two ideas combine:
//   1. MULTIMODAL INPUT  — a ChatMessage whose content is [text + image].
//   2. STRUCTURED OUTPUT — agent.RunAsync<Invoice>() with
//      ChatResponseFormat.ForJsonSchema<Invoice>() so the model must return
//      JSON matching our schema, which MAF deserializes into Invoice.
//
// X-REF Chapter 7: everything about RunAsync<T>, the typed AgentResponse<T>.Result,
// and JSON-schema response formats is taught there. Here we just feed it a picture.
//
// Needs a VISION model (Ollama llava / llama3.2-vision, or cloud gpt-4o), configured
// under "VisionModel" in appsettings.json. If none is reachable, prints a clear
// "[skipped]" note and exits 0.
// ============================================================================

using System.ClientModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Ch11.ImageToTypedData;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
using IHost host = builder.Build();
IConfiguration config = host.Services.GetRequiredService<IConfiguration>();

Console.WriteLine("=== Chapter 11: Image -> Typed Data (invoice extraction) ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

IChatClient? visionClient = BuildVisionClient(config);
if (visionClient is null)
{
    Console.WriteLine("[skipped: configure a vision model]");
    Console.WriteLine("Set a VISION model under \"VisionModel\" in appsettings.json:");
    Console.WriteLine("  ollama -> llava (or llama3.2-vision); or openai/azure/github -> gpt-4o.");
    Console.WriteLine("Pull a local vision model first:  ollama pull llava");
    return;
}

using IChatClient chatClient = visionClient;

// Pretty-printer for the parsed record.
JsonSerializerOptions jsonOptions = new()
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
};

ChatClientAgent agent = new(
    chatClient,
    instructions: """
        You extract structured data from invoice images. Read the image carefully
        and return ONLY the fields requested by the schema. Use numbers (no currency
        symbols) for amounts. If a value is not visible, use an empty string or 0.
        """,
    name: "InvoiceExtractor");

string imagePath = Path.Combine(AppContext.BaseDirectory, "assets", "sample-invoice.png");
if (!File.Exists(imagePath))
{
    Console.WriteLine($"[skipped] sample invoice not found at {imagePath}");
    return;
}

byte[] bytes = await File.ReadAllBytesAsync(imagePath, cts.Token);
Console.WriteLine($"Reading invoice image: {imagePath} ({bytes.Length:N0} bytes)\n");

// The multimodal prompt: an instruction plus the invoice image (local bytes).
ChatMessage message = new(ChatRole.User,
[
    new TextContent("Extract the invoice details from this image into the requested schema."),
    new DataContent(bytes, mediaType: "image/png"),
]);

// Force a JSON shape that matches Invoice. ForJsonSchema<T>() builds the schema
// from the record + [Description] attributes (Chapter 7). We pass it via ChatOptions
// on a per-run options object; RunAsync<Invoice> deserializes the JSON for us.
ChatClientAgentRunOptions runOptions = new(new ChatOptions
{
    ResponseFormat = ChatResponseFormat.ForJsonSchema<Invoice>(jsonOptions),
    Temperature = 0f,
});

try
{
    AgentSession session = await agent.CreateSessionAsync(cts.Token);

    AgentResponse<Invoice> response = await agent.RunAsync<Invoice>(
        message, session, jsonOptions, runOptions, cts.Token);

    Invoice invoice = response.Result;

    Console.WriteLine("--- Parsed Invoice (typed C# record) ---");
    Console.WriteLine($"Invoice #   : {invoice.InvoiceNumber}");
    Console.WriteLine($"Vendor      : {invoice.Vendor}");
    Console.WriteLine($"Date        : {invoice.InvoiceDate}");
    Console.WriteLine($"Currency    : {invoice.Currency}");
    Console.WriteLine($"Line items  : {invoice.LineItems.Count}");
    foreach (InvoiceLine line in invoice.LineItems)
    {
        Console.WriteLine($"  - {line.Quantity,2} x {line.Description,-32} {line.Amount,10:N2}");
    }
    Console.WriteLine($"Total       : {invoice.Total:N2} {invoice.Currency}");
    Console.WriteLine();

    Console.WriteLine("--- Raw JSON (for inspection) ---");
    Console.WriteLine(JsonSerializer.Serialize(invoice, jsonOptions));
    Console.WriteLine();
    Console.WriteLine("=== Invoice extraction complete! ===");
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n[Cancelled by user]");
}
catch (JsonException ex)
{
    Console.WriteLine($"\nJSON deserialization error: {ex.Message}");
    Console.WriteLine("The model's output did not match the Invoice schema. Smaller vision");
    Console.WriteLine("models (llava) can drift; a frontier model (gpt-4o) is far more reliable");
    Console.WriteLine("for structured extraction. Try the cloud provider for crisp results.");
}
catch (Exception ex)
{
    Console.WriteLine($"\n[error] The vision call failed: {ex.Message}");
    Console.WriteLine("Confirm the configured model is a VISION model (llava / gpt-4o).");
}

// ---------------------------------------------------------------
// BuildVisionClient — same gate as Ch11.VisionBasics: returns a vision-capable
// chat client, or null when none is configured/usable.
// ---------------------------------------------------------------
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
                string endpoint = config["VisionModel:Ollama:Endpoint"] ?? "http://localhost:11434";
                return new OllamaSharp.OllamaApiClient(new Uri(endpoint), model);
            }
            return null;
        }
        case "openai":
        {
            string? key = config["VisionModel:OpenAI:ApiKey"];
            if (string.IsNullOrWhiteSpace(key)) return null;
            return new OpenAI.OpenAIClient(key)
                .GetChatClient(config["VisionModel:OpenAI:Model"] ?? "gpt-4o").AsIChatClient();
        }
        case "azure":
        {
            string? endpoint = config["VisionModel:AzureOpenAI:Endpoint"];
            if (string.IsNullOrWhiteSpace(endpoint)) return null;
            return new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential())
                .GetChatClient(config["VisionModel:AzureOpenAI:Model"] ?? "gpt-4o").AsIChatClient();
        }
        case "github":
        {
            string? token = config["VisionModel:GitHub:Token"];
            if (string.IsNullOrWhiteSpace(token)) return null;
            string endpoint = config["VisionModel:GitHub:Endpoint"] ?? "https://models.github.ai/inference";
            return new OpenAI.OpenAIClient(
                    new ApiKeyCredential(token),
                    new OpenAI.OpenAIClientOptions { Endpoint = new Uri(endpoint) })
                .GetChatClient(config["VisionModel:GitHub:Model"] ?? "openai/gpt-4o").AsIChatClient();
        }
        default:
            return null;
    }
}
