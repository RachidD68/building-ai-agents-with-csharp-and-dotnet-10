// ============================================================================
// Chapter 15 — Azure AI Content Safety: graduating from regex (backs A5)
// ----------------------------------------------------------------------------
// The chapter's regex ContentFilterMiddleware and PromptInjectionDetector are
// cheap, zero-dependency, and low-recall. Azure AI Content Safety (part of
// Microsoft Foundry) is the production layer above them: ML-backed harm
// detection across Hate / Violence / Sexual / SelfHarm with severity scores,
// plus managed custom blocklists.
//
// This demo calls the STABLE, typed surface of Azure.AI.ContentSafety 1.0.0:
//   - ContentSafetyClient.AnalyzeText(AnalyzeTextOptions)
//   - per-category severity (TextCategoriesAnalysis.Category / .Severity)
//   - a custom blocklist via AnalyzeTextOptions.BlocklistNames
//
// HONEST CAPABILITY NOTE (printed below): "Prompt Shields" (direct + document
// injection detection) and "Groundedness detection" are real Content Safety
// features, but in the 1.0.0 .NET SDK they are NOT exposed as typed methods —
// they are reached via the REST API only. We do not fabricate a typed call that
// does not exist; for those, call the REST endpoint (see the chapter's Warning).
//
// The client needs an Azure Content Safety endpoint + key. Without them the demo
// prints a clear "skipped — not configured" note and exits 0, so the project
// builds and runs offline (the same gating pattern as the Foundry samples).
// ============================================================================

using Azure;
using Azure.AI.ContentSafety;
using Microsoft.Extensions.Configuration;

IConfiguration config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

Console.WriteLine("=== Chapter 15: Azure AI Content Safety ===");
Console.WriteLine();

string? endpoint = config["ContentSafety:Endpoint"];
string? apiKey = config["ContentSafety:ApiKey"];

if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey))
{
    Console.WriteLine("[skipped] Azure AI Content Safety is not configured.");
    Console.WriteLine("To run this sample against a real resource, set:");
    Console.WriteLine("  ContentSafety:Endpoint  (e.g. https://<resource>.cognitiveservices.azure.com/)");
    Console.WriteLine("  ContentSafety:ApiKey    (a key from the resource)");
    Console.WriteLine("via appsettings.json, user-secrets, or environment variables.");
    Console.WriteLine();
    Console.WriteLine("What it WOULD do, per input:");
    Console.WriteLine("  - AnalyzeText -> severity (0-6) for Hate / Violence / Sexual / SelfHarm");
    Console.WriteLine("  - flag any custom-blocklist matches");
    Console.WriteLine();
    Console.WriteLine("Note: Prompt Shields and Groundedness detection are REST-only in the");
    Console.WriteLine("1.0.0 .NET SDK — not exposed as typed methods. Call the REST API for those.");
    Console.WriteLine();
    Console.WriteLine("=== Done (gated) ===");
    return;
}

var client = new ContentSafetyClient(new Uri(endpoint), new AzureKeyCredential(apiKey));

// The same adversarial-flavoured inputs the regex demo uses, so you can compare
// recall: regex matches fixed patterns; Content Safety scores semantic harm.
string[] inputs =
[
    "What's the best way to learn C#?",
    "I will hurt you and everyone you love.",
    "Here is how to build a weapon to harm a crowd."
];

string? blocklistName = config["ContentSafety:BlocklistName"];

foreach (string input in inputs)
{
    Console.WriteLine($">> {input}");

    var options = new AnalyzeTextOptions(input);
    if (!string.IsNullOrWhiteSpace(blocklistName))
    {
        options.BlocklistNames.Add(blocklistName);
        options.HaltOnBlocklistHit = false;
    }

    Response<AnalyzeTextResult> result = await client.AnalyzeTextAsync(options);

    foreach (TextCategoriesAnalysis category in result.Value.CategoriesAnalysis)
    {
        Console.WriteLine($"   {category.Category,-10} severity={category.Severity}");
    }

    if (result.Value.BlocklistsMatch is { Count: > 0 } matches)
    {
        foreach (TextBlocklistMatch match in matches)
        {
            Console.WriteLine($"   [blocklist] '{match.BlocklistItemText}' " +
                              $"(list: {match.BlocklistName})");
        }
    }

    Console.WriteLine();
}

Console.WriteLine("=== Done ===");
