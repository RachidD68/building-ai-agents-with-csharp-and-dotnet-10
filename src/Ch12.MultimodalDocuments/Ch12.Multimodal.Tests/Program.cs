// ============================================================================
// Testing multimodal agents WITHOUT a vision model (token-free)
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
//   3. What the Ollama connector really sends: DataContent travels as inline
//      base64, UriContent is dropped without an error (a local model never
//      sees, let alone fetches, a remote URL), and a JSON schema becomes
//      Ollama's "format". Checked on the request body, without a server.
//
// Same plain console assertion harness as the multi-agent chapter's tests: Check(...) throws
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

Console.WriteLine("=== Testing multimodal agents (token-free) ===");
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

// ── Test 4: what the Ollama connector puts on the wire ───────────────────────
// A probe of the connector, not of the abstraction: OllamaSharp's IChatClient
// talks to a recording HttpMessageHandler instead of a server, and we read the
// JSON body it would have sent to /api/chat. Version-specific (OllamaSharp
// 5.4.25): re-run this test after a package update.
Console.WriteLine();
Console.WriteLine("Test 4: the Ollama connector's request body (OllamaSharp 5.4.25)");
{
    RecordingHandler wire = new();
    using HttpClient http = new(wire) { BaseAddress = new Uri("http://localhost:11434") };
    using IChatClient ollama = new OllamaSharp.OllamaApiClient(http, "llava");

    await ollama.GetResponseAsync([new ChatMessage(ChatRole.User,
        [new TextContent("q"), new DataContent(new byte[] { 1, 2, 3 }, "image/png")])]);
    Check("DataContent is sent inline as base64 in \"images\"", wire.Body.Contains("\"images\":[\"AQID\"]"));

    await ollama.GetResponseAsync([new ChatMessage(ChatRole.User,
        [new TextContent("q"), new UriContent("https://example.com/cat.jpg", "image/jpeg")])]);
    Check("UriContent is dropped: no \"images\", no URL in the request, no error",
        !wire.Body.Contains("images") && !wire.Body.Contains("example.com"));

    await ollama.GetResponseAsync([new ChatMessage(ChatRole.User, "q")],
        new ChatOptions { ResponseFormat = ChatResponseFormat.ForJsonSchema<Invoice>(), Temperature = 0f });
    Check("ForJsonSchema<T> becomes Ollama's \"format\" schema", wire.Body.Contains("\"format\":{"));
}

// ── Summary / exit code ──────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine($"=== {passed} passed, {failed} failed ===");
Console.WriteLine($"RESULT {passed}/{passed + failed}");
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

// A stand-in for the Ollama server: records the request body and answers with
// one finished, non-streaming /api/chat reply.
sealed class RecordingHandler : HttpMessageHandler
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
