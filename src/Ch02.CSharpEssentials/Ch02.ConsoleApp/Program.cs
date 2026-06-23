// ============================================================================
// Chapter 2 — C# Essentials for AI Development
// This console app demonstrates modern C# features used throughout the book.
// ============================================================================

using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

Console.WriteLine("=== Chapter 2: C# Essentials for AI Development ===\n");

// ----------------------------------------------------------------------------
#region Records and Primary Constructors
// ----------------------------------------------------------------------------
// Records provide value-based equality and immutability — ideal for AI data types
// like chat messages, embeddings, and tool results.

Console.WriteLine("--- Records and Primary Constructors ---");

// A record with positional syntax (concise declaration)
var message = new ChatMessage("user", "Hello, AI!");
Console.WriteLine($"  ChatMessage: Role={message.Role}, Content={message.Content}");

// Records support non-destructive mutation via 'with' expressions
var systemMessage = message with { Role = "system", Content = "You are a helpful assistant." };
Console.WriteLine($"  SystemMessage: Role={systemMessage.Role}, Content={systemMessage.Content}");

// Primary constructor on a class — useful for services
var calculator = new TokenCostCalculator(inputPricePerMillion: 0.15m, outputPricePerMillion: 0.60m);
decimal cost = calculator.Calculate(inputTokens: 1000, outputTokens: 500);
Console.WriteLine($"  Token cost for 1K in / 500 out: ${cost:F6}");

Console.WriteLine();
#endregion

// ----------------------------------------------------------------------------
#region Pattern Matching
// ----------------------------------------------------------------------------
// Pattern matching makes branching on AI response types clean and exhaustive.

Console.WriteLine("--- Pattern Matching ---");

// Switch expressions — perfect for mapping provider names to configurations
string[] providers = ["azure", "openai", "ollama", "unknown"];
foreach (string provider in providers)
{
    string description = provider switch
    {
        "azure" => "Azure OpenAI Service (enterprise)",
        "openai" => "OpenAI direct API",
        "ollama" => "Local Ollama instance",
        _ => $"Unknown provider: {provider}"
    };
    Console.WriteLine($"  {provider} -> {description}");
}

// Property patterns — inspect nested objects
var response = new LlmResponse(200, new TokenUsage(150, 50));
string usageSummary = response switch
{
    { StatusCode: 200, Usage.TotalTokens: < 100 } => "Success (low usage)",
    { StatusCode: 200, Usage.TotalTokens: >= 100 } => "Success (moderate usage)",
    { StatusCode: 429 } => "Rate limited — retry with backoff",
    { StatusCode: >= 500 } => "Server error",
    _ => "Unexpected status"
};
Console.WriteLine($"  Response status {response.StatusCode}: {usageSummary}");

// List patterns — validate batch results
int[] tokenCounts = [150, 200, 175];
string batchResult = tokenCounts switch
{
    [_, _, _] => $"Batch of 3 items, total tokens: {tokenCounts.Sum()}",
    [_] => "Single item batch",
    [] => "Empty batch",
    _ => $"Batch of {tokenCounts.Length} items"
};
Console.WriteLine($"  {batchResult}");

Console.WriteLine();
#endregion

// ----------------------------------------------------------------------------
#region Async/Await with Task and ValueTask
// ----------------------------------------------------------------------------
// All LLM calls are async. Understanding Task vs. ValueTask matters for
// high-throughput scenarios like streaming tokens.

Console.WriteLine("--- Async/Await ---");

// Standard async with Task
string greeting = await GetGreetingAsync("World", CancellationToken.None);
Console.WriteLine($"  Async greeting: {greeting}");

// ValueTask — useful when the result is often synchronous (cached)
int cachedCount = await GetTokenCountAsync("Hello, World!", CancellationToken.None);
Console.WriteLine($"  Token count (cached path): {cachedCount}");

// CancellationToken — every async method in this book accepts one
using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
try
{
    await SimulateLlmCallAsync(cts.Token);
    Console.WriteLine("  LLM call completed within timeout");
}
catch (OperationCanceledException)
{
    Console.WriteLine("  LLM call was cancelled (timeout)");
}

Console.WriteLine();
#endregion

// ----------------------------------------------------------------------------
#region Collection Expressions
// ----------------------------------------------------------------------------
// C# 12+ collection expressions simplify creating lists, arrays, and spans.

Console.WriteLine("--- Collection Expressions ---");

// Array creation with collection expression syntax
string[] models = ["gpt-4o", "gpt-4o-mini", "llama3.2", "phi-4"];
Console.WriteLine($"  Available models: {string.Join(", ", models)}");

// Spread operator to combine collections
string[] localModels = ["llama3.2", "phi-4"];
string[] cloudModels = ["gpt-4o", "gpt-4o-mini"];
string[] allModels = [.. localModels, .. cloudModels];
Console.WriteLine($"  All models: {string.Join(", ", allModels)}");

// Works with List<T> too
List<double> embeddings = [0.1, 0.5, -0.3, 0.8, 0.2];
Console.WriteLine($"  Embedding vector ({embeddings.Count} dims): [{string.Join(", ", embeddings.Select(e => e.ToString("F1")))}]");

Console.WriteLine();
#endregion

// ----------------------------------------------------------------------------
#region Required Members
// ----------------------------------------------------------------------------
// Required members enforce that configuration objects are fully initialized
// at compile time — catching missing API keys early.

Console.WriteLine("--- Required Members ---");

var config = new ProviderConfig
{
    ProviderName = "openai",
    ApiKey = "sk-placeholder-key",
    ModelId = "gpt-4o-mini"
};
Console.WriteLine($"  Config: Provider={config.ProviderName}, Model={config.ModelId}");
// Uncommenting the following would cause a compiler error because ProviderName is required:
// var bad = new ProviderConfig { ApiKey = "key", ModelId = "model" };

Console.WriteLine();
#endregion

// ----------------------------------------------------------------------------
#region Extension Methods
// ----------------------------------------------------------------------------
// Extension methods add capabilities to types you don't own.
// We use them throughout the book to extend IChatClient, IServiceCollection, etc.

Console.WriteLine("--- Extension Methods ---");

string longText = "Agentic AI is transforming how developers build intelligent applications with .NET.";
string truncated = longText.TruncateToTokenEstimate(5);
Console.WriteLine($"  Original:  \"{longText}\"");
Console.WriteLine($"  Truncated: \"{truncated}\"");

double[] vector = [0.1, 0.2, 0.3, 0.4];
string formatted = vector.FormatAsVector();
Console.WriteLine($"  Vector: {formatted}");

Console.WriteLine();
#endregion

// ----------------------------------------------------------------------------
#region Nullable Reference Types
// ----------------------------------------------------------------------------
// Nullable reference types prevent null-reference exceptions at compile time.
// Critical when handling optional LLM response fields.

Console.WriteLine("--- Nullable Reference Types ---");

LlmResult successResult = new("Here are three facts about .NET...", FinishReason: "stop");
LlmResult emptyResult = new(null, FinishReason: "length");

PrintResult(successResult);
PrintResult(emptyResult);

static void PrintResult(LlmResult result)
{
    // Null-conditional and null-coalescing patterns
    string display = result.Content?.Trim() ?? "[no content returned]";
    int length = result.Content?.Length ?? 0;
    Console.WriteLine($"  Result ({result.FinishReason}): {display} ({length} chars)");
}

Console.WriteLine();
#endregion

// ----------------------------------------------------------------------------
#region JSON Serialization with System.Text.Json
// ----------------------------------------------------------------------------
// Agents serialize constantly: tool arguments in, tool results out, typed
// responses via RunAsync<T>(). JsonSerializerOptions.Web is the exact config
// MAF and this book's tools use — camelCase names, case-insensitive reads.

Console.WriteLine("--- JSON Serialization ---");

var toolCall = new ToolCall(
    Name: "get_weather",
    Arguments: """{ "city": "Seattle" }""",
    Status: ToolStatus.Succeeded);

JsonSerializerOptions webOptions = JsonSerializerOptions.Web;

string toolJson = JsonSerializer.Serialize(toolCall, webOptions);
Console.WriteLine($"  Serialized:   {toolJson}");

// Round-trip back. The enum arrives as a string (thanks to the converter),
// and "tool" maps to Name via [JsonPropertyName].
ToolCall? roundTripped = JsonSerializer.Deserialize<ToolCall>(toolJson, webOptions);
Console.WriteLine($"  Deserialized: {roundTripped?.Name} -> {roundTripped?.Status}");

Console.WriteLine();
#endregion

// ----------------------------------------------------------------------------
#region Raw String Literals
// ----------------------------------------------------------------------------
// Agent instructions are the first multi-line strings you'll write. Raw string
// literals ("""...""") keep them readable — no \n, no escaping — and the
// indentation is stripped to the closing-delimiter column.

Console.WriteLine("--- Raw String Literals ---");

string agentName = "Contoso Assistant";
string instructions = $"""
    You are {agentName}, a support agent for Contoso.
    Rules:
      - Answer only from the provided knowledge base.
      - If you don't know, say so and offer to open a ticket.
      - Never invent order numbers or prices.
    """;
Console.WriteLine(instructions);

Console.WriteLine();
#endregion

// ----------------------------------------------------------------------------
#region Streaming with IAsyncEnumerable
// ----------------------------------------------------------------------------
// IAsyncEnumerable<T> is how MAF streams tokens via RunStreamingAsync. You
// consume it with await foreach, accumulating partial text as updates arrive.

Console.WriteLine("--- Streaming with IAsyncEnumerable ---");

using CancellationTokenSource streamCts = new(TimeSpan.FromSeconds(5));
StringBuilder assembled = new();
Console.Write("  ");
await foreach (string chunk in StreamWordsAsync("Agents stream their answers token by token.", streamCts.Token)
                   .WithCancellation(streamCts.Token))
{
    Console.Write(chunk);          // each chunk prints the moment it arrives
    assembled.Append(chunk);
}
Console.WriteLine();
Console.WriteLine($"  Assembled length: {assembled.Length} chars");

Console.WriteLine();
#endregion

// ----------------------------------------------------------------------------
#region Exception Handling for Async and Network Code
// ----------------------------------------------------------------------------
// Model and network calls fail in specific ways. Exception filters keep the
// handling precise, and cancellation must be told apart from a real error.

Console.WriteLine("--- Exception Handling ---");

using CancellationTokenSource exCts = new(TimeSpan.FromMilliseconds(50));
try
{
    // The call needs 200ms; the 50ms token fires first -> cancellation.
    await CallFlakyServiceAsync(exCts.Token);
}
catch (OperationCanceledException)
{
    // Cancellation/timeout is not an error to swallow — surface it and stop.
    Console.WriteLine("  Call cancelled (timeout) — surfaced, not swallowed");
}
catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
{
    // Exception filter: only a 429 reaches this arm.
    Console.WriteLine("  Rate limited (429) — back off and retry");
}
catch (HttpRequestException ex)
{
    Console.WriteLine($"  Request failed: {ex.StatusCode}");
}

Console.WriteLine();
#endregion

Console.WriteLine("=== All demos complete! ===");

// ============================================================================
// Static Local Functions (must appear before type declarations)
// ============================================================================

#region Async Helper Methods

/// <summary>
/// Simulates an async LLM greeting call.
/// </summary>
static async Task<string> GetGreetingAsync(string name, CancellationToken cancellationToken)
{
    // Simulate network latency
    await Task.Delay(50, cancellationToken);
    return $"Hello, {name}! I am your AI assistant.";
}

/// <summary>
/// Demonstrates ValueTask — returns synchronously when result is cached.
/// </summary>
static ValueTask<int> GetTokenCountAsync(string text, CancellationToken cancellationToken)
{
    // In real code, this might check a cache first (synchronous path)
    // and only go async on a cache miss.
    _ = cancellationToken; // Acknowledge the token even when unused
    int estimate = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
    return ValueTask.FromResult(estimate);
}

/// <summary>
/// Simulates a long-running LLM call that respects cancellation.
/// </summary>
static async Task SimulateLlmCallAsync(CancellationToken cancellationToken)
{
    // Simulate a 200ms LLM call
    await Task.Delay(200, cancellationToken);
}

/// <summary>
/// Simulates a streamed model response. The [EnumeratorCancellation] attribute
/// flows the await-foreach token into the async iterator — the producer shape
/// MAF uses for RunStreamingAsync.
/// </summary>
static async IAsyncEnumerable<string> StreamWordsAsync(
    string text,
    [EnumeratorCancellation] CancellationToken cancellationToken)
{
    foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
    {
        await Task.Delay(40, cancellationToken);   // simulate token latency
        yield return word + " ";
    }
}

/// <summary>
/// Simulates a slow network call that respects cancellation. Delays longer than
/// the caller's timeout so the token cancels it.
/// </summary>
static async Task CallFlakyServiceAsync(CancellationToken cancellationToken)
{
    await Task.Delay(200, cancellationToken);
}

#endregion

// ============================================================================
// Supporting Types
// ============================================================================

#region Type Definitions

/// <summary>
/// Represents a chat message with a role and content.
/// Positional record — concise and immutable.
/// </summary>
/// <param name="Role">The message role (system, user, assistant).</param>
/// <param name="Content">The message text content.</param>
internal sealed record ChatMessage(string Role, string Content);

/// <summary>
/// Token usage information from an LLM response.
/// </summary>
/// <param name="InputTokens">Number of tokens in the prompt.</param>
/// <param name="OutputTokens">Number of tokens in the completion.</param>
internal sealed record TokenUsage(int InputTokens, int OutputTokens)
{
    /// <summary>Gets the total token count.</summary>
    public int TotalTokens => InputTokens + OutputTokens;
}

/// <summary>
/// Simulated LLM API response with status code and usage data.
/// </summary>
/// <param name="StatusCode">HTTP status code.</param>
/// <param name="Usage">Token usage details.</param>
internal sealed record LlmResponse(int StatusCode, TokenUsage Usage);

/// <summary>
/// An LLM completion result with nullable content.
/// </summary>
/// <param name="Content">The generated text, or null if none was returned.</param>
/// <param name="FinishReason">Why the model stopped generating.</param>
internal sealed record LlmResult(string? Content, string FinishReason);

/// <summary>
/// Demonstrates required members for provider configuration.
/// </summary>
internal sealed class ProviderConfig
{
    /// <summary>The LLM provider name (e.g., "azure", "openai").</summary>
    public required string ProviderName { get; init; }

    /// <summary>The API key for authentication.</summary>
    public required string ApiKey { get; init; }

    /// <summary>The model identifier to use.</summary>
    public required string ModelId { get; init; }
}

/// <summary>
/// Primary constructor example — a token cost calculator service.
/// </summary>
/// <param name="inputPricePerMillion">Cost per 1M input tokens.</param>
/// <param name="outputPricePerMillion">Cost per 1M output tokens.</param>
internal sealed class TokenCostCalculator(decimal inputPricePerMillion, decimal outputPricePerMillion)
{
    /// <summary>
    /// Calculates the total cost for a given token usage.
    /// </summary>
    /// <param name="inputTokens">Number of input tokens.</param>
    /// <param name="outputTokens">Number of output tokens.</param>
    /// <returns>The total cost in dollars.</returns>
    public decimal Calculate(int inputTokens, int outputTokens)
    {
        return (inputTokens * inputPricePerMillion / 1_000_000m)
             + (outputTokens * outputPricePerMillion / 1_000_000m);
    }
}

/// <summary>
/// A tool call, shaped the way MAF serializes tool arguments and results.
/// [JsonPropertyName] renames a property on the wire; the enum serializes as a
/// string via the converter below.
/// </summary>
/// <param name="Name">The tool name (serialized as "tool").</param>
/// <param name="Arguments">The raw JSON arguments passed to the tool.</param>
/// <param name="Status">How the call finished.</param>
internal sealed record ToolCall(
    [property: JsonPropertyName("tool")] string Name,
    string Arguments,
    ToolStatus Status);

/// <summary>How a tool call finished. Serialized as a string, not an integer.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum ToolStatus
{
    Pending,
    Succeeded,
    Failed
}

#endregion

#region Extension Methods

/// <summary>
/// Extension methods for common AI-related string operations.
/// </summary>
internal static class StringExtensions
{
    /// <summary>
    /// Truncates text to a word count as a rough proxy for token count.
    /// NOT a real BPE tokenizer — see Chapter 3 and Microsoft.ML.Tokenizers
    /// for accurate token counting.
    /// </summary>
    /// <param name="text">The input text.</param>
    /// <param name="maxWords">Maximum word count to keep.</param>
    /// <returns>The truncated text.</returns>
    public static string TruncateToTokenEstimate(this string text, int maxWords)
    {
        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= maxWords)
        {
            return text;
        }
        return string.Join(' ', words.Take(maxWords)) + "...";
    }
}

/// <summary>
/// Extension methods for numeric array formatting.
/// </summary>
internal static class VectorExtensions
{
    /// <summary>
    /// Formats a double array as a vector string representation.
    /// </summary>
    /// <param name="vector">The vector to format.</param>
    /// <returns>A string like "[0.10, 0.20, 0.30]".</returns>
    public static string FormatAsVector(this double[] vector)
    {
        return $"[{string.Join(", ", vector.Select(v => v.ToString("F2")))}]";
    }
}

#endregion
