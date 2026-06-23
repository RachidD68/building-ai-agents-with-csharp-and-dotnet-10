// ============================================================================
// Chapter 2 — Exercise 04: JSON Round-Trip (Basic)
// Serialize a set of records to JSON with JsonSerializerOptions.Web, deserialize
// them back, and assert a clean round-trip. Demonstrates [JsonPropertyName] and
// enum-as-string — the exact serialization shape MAF uses for tool calls.
//
// Records give value-based equality for free, so `restored == original` IS the
// round-trip assertion (every scalar member must survive serialize+deserialize).
// ============================================================================

using System.Text.Json;
using System.Text.Json.Serialization;

Console.WriteLine("=== Exercise 04: JSON Round-Trip ===\n");

JsonSerializerOptions options = JsonSerializerOptions.Web;

ToolInvocation[] calls =
[
    new("get_weather", "city=Seattle", ToolOutcome.Succeeded),
    new("send_email", "to=ops@contoso.com", ToolOutcome.Failed),
    new("lookup_order", "id=A-1421", ToolOutcome.Pending),
];

int passed = 0;
foreach (ToolInvocation original in calls)
{
    string json = JsonSerializer.Serialize(original, options);
    ToolInvocation? restored = JsonSerializer.Deserialize<ToolInvocation>(json, options);

    bool clean = restored == original;   // value equality — no field-by-field compare needed
    Console.WriteLine($"  {original.Name,-13} -> {json}");
    Console.WriteLine($"       round-trip {(clean ? "OK" : "MISMATCH")}");
    if (clean) passed++;
}

Console.WriteLine($"\n{passed}/{calls.Length} records round-tripped cleanly.");
return passed == calls.Length ? 0 : 1;

// ----------------------------------------------------------------------------

/// <summary>A tool invocation, serialized the way MAF shapes tool calls.</summary>
/// <param name="Name">The tool name (serialized as "tool").</param>
/// <param name="Argument">A single key=value argument, kept scalar so the record stays value-equal.</param>
/// <param name="Outcome">How the call finished (serialized as a string).</param>
internal sealed record ToolInvocation(
    [property: JsonPropertyName("tool")] string Name,
    string Argument,
    ToolOutcome Outcome);

/// <summary>How a tool call finished. Serialized as a string, not an integer.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum ToolOutcome
{
    Pending,
    Succeeded,
    Failed
}
