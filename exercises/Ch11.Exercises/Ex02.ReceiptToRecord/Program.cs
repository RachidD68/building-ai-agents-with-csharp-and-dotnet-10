// ============================================================================
// Exercise 02 — Receipt / Invoice to a Typed Record (Intermediate)
// ----------------------------------------------------------------------------
// Extends the flagship Ch11.ImageToTypedData: read the invoice image and extract
// a typed Receipt record via RunAsync<T>() + ChatResponseFormat.ForJsonSchema<T>().
// The twist over the flagship: we ALSO compute the line-item sum ourselves and
// flag whether it reconciles with the model-extracted total — a cheap, useful
// guard you'd want in any real "read a receipt" pipeline.
//
// X-REF Chapter 7 for RunAsync<T> and JSON-schema response formats.
//
// Needs a vision model under "VisionModel" (ollama llava / cloud gpt-4o). If none
// is configured, prints "[skipped]" and exits 0.
// ============================================================================

using System.ClientModel;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
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

Console.WriteLine("=== Exercise 02: Receipt -> Typed Record ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

IChatClient? visionClient = BuildVisionClient(config);
if (visionClient is null)
{
    Console.WriteLine("[skipped: configure a vision model]");
    Console.WriteLine("Set \"VisionModel\" (ollama llava, or openai/azure/github gpt-4o). Local: ollama pull llava");
    return;
}

using IChatClient chatClient = visionClient;

JsonSerializerOptions jsonOptions = new()
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
};

string imagePath = Path.Combine(AppContext.BaseDirectory, "assets", "sample-invoice.png");
if (!File.Exists(imagePath))
{
    Console.WriteLine($"[skipped] sample invoice not found at {imagePath}");
    return;
}

byte[] bytes = await File.ReadAllBytesAsync(imagePath, cts.Token);

ChatClientAgent agent = new(
    chatClient,
    instructions: "You read receipts and invoices from images and return structured data. "
                + "Use numbers without currency symbols. If a field is missing, use 0 or an empty string.",
    name: "ReceiptReader");

ChatMessage message = new(ChatRole.User,
[
    new TextContent("Extract this receipt into the requested schema."),
    new DataContent(bytes, mediaType: "image/png"),
]);

ChatClientAgentRunOptions runOptions = new(new ChatOptions
{
    ResponseFormat = ChatResponseFormat.ForJsonSchema<Receipt>(jsonOptions),
    Temperature = 0f,
});

try
{
    AgentSession session = await agent.CreateSessionAsync(cts.Token);
    AgentResponse<Receipt> response = await agent.RunAsync<Receipt>(message, session, jsonOptions, runOptions, cts.Token);
    Receipt receipt = response.Result;

    Console.WriteLine($"Vendor   : {receipt.Vendor}");
    Console.WriteLine($"Date     : {receipt.Date}");
    Console.WriteLine("Items    :");
    decimal computed = 0m;
    foreach (ReceiptItem item in receipt.Items)
    {
        Console.WriteLine($"  - {item.Name,-34} {item.Price,10:N2}");
        computed += item.Price;
    }
    Console.WriteLine($"Total    : {receipt.Total:N2}");

    // The reconciliation guard: does the sum of line items match the stated total?
    // (Allow a small tolerance for tax/rounding — here the invoice total includes tax.)
    Console.WriteLine();
    Console.WriteLine($"Line-item sum: {computed:N2}   Stated total: {receipt.Total:N2}");
    Console.WriteLine(computed == receipt.Total
        ? "Reconciles exactly."
        : "Does NOT match exactly (expected when tax/shipping is added on top — inspect).");

    Console.WriteLine("\n--- Raw JSON ---");
    Console.WriteLine(JsonSerializer.Serialize(receipt, jsonOptions));
    Console.WriteLine("\n=== Done ===");
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n[Cancelled]");
}
catch (JsonException ex)
{
    Console.WriteLine($"\nJSON error: {ex.Message}");
    Console.WriteLine("Small vision models can drift; gpt-4o is far more reliable for extraction.");
}
catch (Exception ex)
{
    Console.WriteLine($"\n[error] Vision call failed: {ex.Message}");
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

// The typed shape we extract into.
public sealed record Receipt
{
    [Description("The vendor or store name on the receipt.")]
    public string Vendor { get; init; } = "";

    [Description("The receipt or invoice date, ISO format if possible.")]
    public string Date { get; init; } = "";

    [Description("The purchased items.")]
    public List<ReceiptItem> Items { get; init; } = [];

    [Description("The grand total, as a number with no currency symbol.")]
    public decimal Total { get; init; }
}

public sealed record ReceiptItem
{
    [Description("The item description or name.")]
    public string Name { get; init; } = "";

    [Description("The line amount, as a number with no currency symbol.")]
    public decimal Price { get; init; }
}
