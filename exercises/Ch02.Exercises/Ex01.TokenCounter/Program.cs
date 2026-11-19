// ============================================================================
// Exercise 01 — Token Counter (Basic)
// Extends the companion EstimateTokenCount method with a content-type hint
// ("prose", "code", "json") and per-type divisors, then processes a folder of
// .txt/.cs/.json files and prints a table of file -> estimated tokens ->
// estimated GPT-5.6 Terra input cost.
// ============================================================================

// GPT-5.6 Terra input rate, per 1M tokens (verify current rates before relying on them).
const decimal InputRatePer1M = 2.00m;

Console.WriteLine("=== Exercise: Token Counter — Content-Type-Aware Estimator ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// Default to the bundled samples folder (copied next to the binary at build
// time). Pass a different folder as the first argument to scan your own files:
//   dotnet run -- C:\path\to\folder
string folder = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "samples");

if (!Directory.Exists(folder))
{
    Console.WriteLine($"Folder not found: {folder}");
    return 1;
}

string[] files = [.. Directory.EnumerateFiles(folder)
    .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".txt" or ".cs" or ".json")
    .Order(StringComparer.OrdinalIgnoreCase)];

if (files.Length == 0)
{
    Console.WriteLine($"No .txt, .cs, or .json files found in: {folder}");
    return 1;
}

Console.WriteLine($"Scanning: {folder}\n");

Console.WriteLine($"  {"File",-16} {"Type",-6} {"Chars",8} {"Est. tokens",12} {"Input cost ($)",15}");
Console.WriteLine($"  {new string('-', 16)} {new string('-', 6)} {new string('-', 8)} {new string('-', 12)} {new string('-', 15)}");

int totalTokens = 0;
decimal totalCost = 0m;

foreach (string file in files)
{
    string text = await File.ReadAllTextAsync(file, cts.Token);

    string contentType = GetContentTypeHint(file);
    int tokens = EstimateTokenCount(text, contentType);
    decimal cost = tokens * InputRatePer1M / 1_000_000m;

    totalTokens += tokens;
    totalCost += cost;

    Console.WriteLine($"  {Path.GetFileName(file),-16} {contentType,-6} {text.Length,8:N0} {tokens,12:N0} {cost,15:F6}");
}

Console.WriteLine($"  {new string('-', 16)} {new string('-', 6)} {new string('-', 8)} {new string('-', 12)} {new string('-', 15)}");
Console.WriteLine($"  {"TOTAL",-16} {"",-6} {"",8} {totalTokens,12:N0} {totalCost,15:F6}");

Console.WriteLine("\n=== Exercise complete! ===");

return 0;

// ---------------------------------------------------------------
// Helpers — a 20-line method beats a framework
// ---------------------------------------------------------------

/// <summary>
/// Maps a file extension to a content-type hint for the estimator.
/// </summary>
/// <param name="filePath">The file whose extension supplies the hint.</param>
/// <returns>"prose", "code", or "json".</returns>
static string GetContentTypeHint(string filePath) =>
    Path.GetExtension(filePath).ToLowerInvariant() switch
    {
        ".cs" => "code",
        ".json" => "json",
        _ => "prose"   // .txt (and anything unrecognized) reads like prose
    };

/// <summary>
/// Estimates the token count for a text, adjusting the chars-per-token divisor
/// by content type: prose tokenizes at ~4 chars/token, code at ~3 (symbols and
/// identifiers split more), and JSON at ~2.5 (punctuation-heavy).
/// </summary>
/// <param name="text">The text to estimate tokens for.</param>
/// <param name="contentTypeHint">"prose", "code", or "json".</param>
/// <returns>The estimated number of tokens.</returns>
static int EstimateTokenCount(string text, string contentTypeHint)
{
    if (string.IsNullOrEmpty(text)) return 0;

    double divisor = contentTypeHint switch
    {
        "code" => 3.0,
        "json" => 2.5,
        _ => 4.0   // "prose" — the classic rule of thumb
    };

    return Math.Max(1, (int)Math.Ceiling(text.Length / divisor));
}
