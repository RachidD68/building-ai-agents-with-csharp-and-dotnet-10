// ============================================================================
// Exercise 02 — Receipt / Invoice to a Typed Record (Intermediate)
// ----------------------------------------------------------------------------
// Extends Ch12.ImageToTypedData: read the invoice image into a typed Receipt
// record with RunAsync<T>() + ChatResponseFormat.ForJsonSchema<T>(), then add a
// RECONCILIATION GUARD after extraction: the items plus the tax must equal the
// total. A mismatch marks the total ToVerify for a person. The guard never
// "corrects" the number: the total shown is the one the model read.
//
//   dotnet run                    the sample invoice through the configured vision model
//   dotnet run -- --self-test     scripted replies, no network; RESULT n/n
//
// Needs a vision model under "VisionModel" (ollama llava / cloud gpt-5.6-terra). If none
// is configured, prints "[skipped]" and exits 0.
// ============================================================================

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

if (args.Contains("--self-test")) return await SelfTest.RunAsync();

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
    Console.WriteLine("Set \"VisionModel\" (ollama llava, or openai gpt-5.6-terra).");
    return 0;
}
using IChatClient chatClient = visionClient;

string imagePath = Path.Combine(AppContext.BaseDirectory, "assets", "sample-invoice.png");
byte[] bytes = await File.ReadAllBytesAsync(imagePath, cts.Token);
try
{
    Receipt receipt = await ReceiptExtractor.ExtractAsync(chatClient, bytes, cts.Token);
    Console.WriteLine($"Vendor   : {receipt.Vendor}");
    Console.WriteLine($"Date     : {receipt.Date}");
    foreach (ReceiptItem item in receipt.Items) Console.WriteLine($"  - {item.Name,-34} {item.Price,10:N2}");
    Console.WriteLine($"Tax      : {receipt.Tax:N2}");
    Console.WriteLine($"Total    : {receipt.Total:N2} (as read)");
    Console.WriteLine($"Guard    : {Reconciliation.Check(receipt)}");
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n[Cancelled]");
}
catch (JsonException ex)
{
    Console.WriteLine($"\nThe reply was not a Receipt ({ex.Message}). Nothing was extracted; nothing is guessed.");
}
catch (Exception ex)
{
    Console.WriteLine($"\n[error] Vision call failed: {ex.Message}");
}
return 0;

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

internal static class ReceiptExtractor
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<Receipt> ExtractAsync(IChatClient client, byte[] png, CancellationToken ct)
    {
        ChatClientAgent agent = new(client,
            instructions: "You read receipts and invoices from images. Copy amounts exactly as printed, "
                        + "as plain numbers without currency symbols. Never compute or correct a value.",
            name: "ReceiptReader");
        ChatMessage message = new(ChatRole.User,
            [new TextContent("Extract this receipt into the requested schema."), new DataContent(png, "image/png")]);
        ChatClientAgentRunOptions options = new(new ChatOptions
        {
            ResponseFormat = ChatResponseFormat.ForJsonSchema<Receipt>(Json),
            Temperature = 0f,
        });
        AgentResponse<Receipt> response = await agent.RunAsync<Receipt>(
            message, await agent.CreateSessionAsync(ct), Json, options, ct);
        return response.Result;
    }
}

/// <summary>The guard: a mismatch is reported, never repaired.</summary>
internal static class Reconciliation
{
    public static string Check(Receipt r)
    {
        decimal expected = r.Items.Sum(i => i.Price) + r.Tax;
        return expected == r.Total
            ? $"Present: items {r.Items.Sum(i => i.Price):N2} + tax {r.Tax:N2} = total {r.Total:N2}"
            : $"ToVerify: items + tax = {expected:N2}, but the total read is {r.Total:N2}";
    }
}

internal static class SelfTest
{
    public static async Task<int> RunAsync()
    {
        Console.WriteLine("=== Exercise 02: reconciliation guard (self-test) ===");
        int passed = 0, total = 0;
        void Expect(string name, bool ok) { total++; if (ok) passed++; Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}"); }

        // Scripted replies for the sample invoice: 178 + 120 + 340 + 84 = 722.00, tax 57.76, total 779.76.
        const string items = """[{"name":"Mechanical keyboard","price":178.00},{"name":"USB-C docking station","price":120.00},{"name":"27-inch 4K monitor","price":340.00},{"name":"Laptop stand","price":84.00}]""";
        Receipt good = await Extract($$"""{"vendor":"Contoso Ltd.","date":"2026-06-18","items":{{items}},"tax":57.76,"total":779.76}""");
        Expect($"a reading that adds up -> {Reconciliation.Check(good)}", Reconciliation.Check(good).StartsWith("Present"));

        Receipt typo = await Extract($$"""{"vendor":"Contoso Ltd.","date":"2026-06-18","items":{{items}},"tax":57.76,"total":779.67}""");
        Expect($"a misread total -> {Reconciliation.Check(typo)}", Reconciliation.Check(typo).StartsWith("ToVerify"));
        Expect("the misread total is kept as read (779.67), not replaced by 779.76", typo.Total == 779.67m);

        Receipt noTax = await Extract($$"""{"vendor":"Contoso Ltd.","date":"2026-06-18","items":{{items}},"tax":0,"total":779.76}""");
        Expect("a reading that dropped the tax line is ToVerify", Reconciliation.Check(noTax).StartsWith("ToVerify"));

        Console.WriteLine($"\nRESULT {passed}/{total}");
        return passed == total ? 0 : 1;
    }

    static Task<Receipt> Extract(string reply) =>
        ReceiptExtractor.ExtractAsync(new ScriptedClient(reply), [0x89, 0x50, 0x4E, 0x47], CancellationToken.None);
}

/// <summary>A fake vision model that always returns one scripted reply.</summary>
internal sealed class ScriptedClient(string reply) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield return new ChatResponseUpdate(ChatRole.Assistant, reply);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
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

    [Description("The tax amount printed on the receipt, 0 if none is printed.")]
    public decimal Tax { get; init; }

    [Description("The grand total, as printed, a number with no currency symbol.")]
    public decimal Total { get; init; }
}

public sealed record ReceiptItem
{
    [Description("The item description or name.")]
    public string Name { get; init; } = "";

    [Description("The line amount, as a number with no currency symbol.")]
    public decimal Price { get; init; }
}
