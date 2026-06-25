// ============================================================================
// Chapter 11 — Testing multimodal agents WITHOUT a vision model (token-free)
// ----------------------------------------------------------------------------
// You don't need a GPU, a key, or llava to test that your multimodal plumbing
// is correct. A fake IChatClient lets you assert two things deterministically:
//
//   1. The multimodal ChatMessage you build actually carries the right parts —
//      a TextContent plus a UriContent (hosted URL) or DataContent (local bytes).
//      Our CapturingChatClient records the messages the agent sends, so we can
//      inspect them after the run.
//
//   2. The typed-extraction path deserializes — when the model returns JSON,
//      RunAsync<Invoice>() yields a populated Invoice record.
//
// Same plain console assertion harness as the Chapter 12 tests: Check(...) throws
// nothing, just tallies; a non-zero exit code if anything failed, so CI catches it.
// NO real model calls anywhere in this file.
// ============================================================================

using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

int passed = 0;
int failed = 0;

void Check(string name, bool condition)
{
    if (condition) { passed++; Console.WriteLine($"  PASS  {name}"); }
    else           { failed++; Console.WriteLine($"  FAIL  {name}"); }
}

Console.WriteLine("=== Chapter 11: Testing multimodal agents (token-free) ===");
Console.WriteLine();

// ── Test 1: a hosted-URL multimodal message carries text + UriContent ────────
Console.WriteLine("Test 1: multimodal message (hosted URL) is built correctly");
{
    CapturingChatClient fake = new(_ => "a cat");
    ChatClientAgent agent = new(fake, instructions: "ignored", name: "Vision");

    ChatMessage message = new(ChatRole.User,
    [
        new TextContent("What animal is this?"),
        new UriContent("https://example.com/cat.jpg", mediaType: "image/jpeg"),
    ]);

    AgentSession session = await agent.CreateSessionAsync();
    AgentResponse response = await agent.RunAsync(message, session);

    // The agent forwarded our message to the chat client. Find the user message
    // among everything the client saw and inspect its content parts.
    ChatMessage? userMsg = LastUserMessage(fake.LastMessages);
    bool hasText = userMsg is not null && userMsg.Contents.OfType<TextContent>().Any();
    bool hasImageUri = userMsg is not null && userMsg.Contents.OfType<UriContent>().Any();

    Check("user message reached the client", userMsg is not null);
    Check("message carries a TextContent part", hasText);
    Check("message carries a UriContent (hosted image) part", hasImageUri);
    Check("agent returned the scripted description", response.Text.Trim() == "a cat");
}

// ── Test 2: a local-file multimodal message carries text + DataContent ───────
Console.WriteLine();
Console.WriteLine("Test 2: multimodal message (local bytes) is built correctly");
{
    CapturingChatClient fake = new(_ => "a book cover");
    ChatClientAgent agent = new(fake, instructions: "ignored", name: "Vision");

    byte[] fakePng = [0x89, 0x50, 0x4E, 0x47]; // PNG magic bytes — stand-in for a real file
    ChatMessage message = new(ChatRole.User,
    [
        new TextContent("Describe this cover."),
        new DataContent(fakePng, mediaType: "image/png"),
    ]);

    AgentSession session = await agent.CreateSessionAsync();
    await agent.RunAsync(message, session);

    ChatMessage? userMsg = LastUserMessage(fake.LastMessages);
    DataContent? data = userMsg?.Contents.OfType<DataContent>().FirstOrDefault();

    Check("message carries a DataContent (local bytes) part", data is not null);
    Check("DataContent preserves the media type", data?.MediaType == "image/png");
    Check("DataContent inlined the original bytes", data is not null && data.Data.Length == 4);
}

// ── Test 3: typed extraction path deserializes the model's JSON ──────────────
// The model "reads the invoice" and returns JSON; RunAsync<Invoice> deserializes
// it. We script the JSON, so this verifies the deserialization wiring only.
Console.WriteLine();
Console.WriteLine("Test 3: typed-extraction path deserializes into Invoice");
{
    const string cannedJson = """
        {
          "invoiceNumber": "INV-2026-0454",
          "vendor": "Contoso Ltd.",
          "invoiceDate": "2026-06-18",
          "currency": "USD",
          "lineItems": [
            { "description": "Mechanical keyboard", "quantity": 2, "amount": 178.00 },
            { "description": "USB-C docking station", "quantity": 1, "amount": 120.00 }
          ],
          "total": 779.76
        }
        """;

    CapturingChatClient fake = new(_ => cannedJson);
    ChatClientAgent agent = new(fake, instructions: "ignored", name: "Extractor");

    JsonSerializerOptions opts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    ChatMessage message = new(ChatRole.User,
    [
        new TextContent("Extract the invoice."),
        new DataContent(new byte[] { 1, 2, 3 }, mediaType: "image/png"),
    ]);

    AgentSession session = await agent.CreateSessionAsync();
    AgentResponse<Invoice> response = await agent.RunAsync<Invoice>(message, session, opts);
    Invoice invoice = response.Result;

    Check("invoice number deserialized", invoice.InvoiceNumber == "INV-2026-0454");
    Check("vendor deserialized", invoice.Vendor == "Contoso Ltd.");
    Check("line items deserialized", invoice.LineItems.Count == 2);
    Check("total deserialized", invoice.Total == 779.76m);
}

// ── Summary / exit code ──────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine($"=== {passed} passed, {failed} failed ===");
return failed == 0 ? 0 : 1;

// ── Helpers ──────────────────────────────────────────────────────────────────
static ChatMessage? LastUserMessage(IEnumerable<ChatMessage>? messages)
{
    ChatMessage? found = null;
    if (messages is null) return null;
    foreach (ChatMessage m in messages)
    {
        if (m.Role == ChatRole.User) found = m;
    }
    return found;
}

// The shape we extract into — a trimmed copy of the flagship's Invoice record,
// kept local so the test project has no project reference to the demo.
public sealed record Invoice
{
    public string InvoiceNumber { get; init; } = "";
    public string Vendor { get; init; } = "";
    public string InvoiceDate { get; init; } = "";
    public string Currency { get; init; } = "";
    public List<InvoiceLine> LineItems { get; init; } = [];
    public decimal Total { get; init; }
}

public sealed record InvoiceLine
{
    public string Description { get; init; } = "";
    public int Quantity { get; init; }
    public decimal Amount { get; init; }
}

// ============================================================================
// CapturingChatClient — a fake IChatClient that (a) records the messages it is
// asked to respond to, so tests can inspect the multimodal parts, and (b) returns
// a scripted reply. Implements both blocking and streaming paths.
// ============================================================================
sealed class CapturingChatClient(Func<IEnumerable<ChatMessage>, string> script) : IChatClient
{
    public IReadOnlyList<ChatMessage>? LastMessages { get; private set; }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        LastMessages = messages.ToList();
        string reply = script(messages);
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        LastMessages = messages.ToList();
        yield return new ChatResponseUpdate(ChatRole.Assistant, script(messages));
        await Task.CompletedTask;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
