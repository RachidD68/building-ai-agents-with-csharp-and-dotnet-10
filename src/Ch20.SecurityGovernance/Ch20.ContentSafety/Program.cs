// ============================================================================
// Azure AI Content Safety: one more detection signal (optional, Azure only)
// ----------------------------------------------------------------------------
// Uses the typed surface of Azure.AI.ContentSafety 1.0.0:
//   ContentSafetyClient.AnalyzeTextAsync(AnalyzeTextOptions)
//   per-category severity (TextCategoriesAnalysis.Category / .Severity)
//   custom blocklists (AnalyzeTextOptions.BlocklistNames)
// Prompt Shields and groundedness detection are not typed methods in the
// 1.0.0 .NET SDK; they are REST calls, and this sample does not make them.
//
// The result becomes a ModerationSignal (flagged or not, and why). Like the
// regex detectors, it is a signal: it never decides whether an action runs.
//
// Without ContentSafety:Endpoint the live run prints "skipped" and exits 0.
// Auth: DefaultAzureCredential, or ContentSafety:ApiKey when one is set.
//   dotnet run -- --self-test     offline: options and signal mapping, no Azure call
// ============================================================================

using Azure;
using Azure.AI.ContentSafety;
using Azure.Identity;
using Ch20.ContentSafety;
using Microsoft.Extensions.Configuration;

if (args.Contains("--self-test"))
    return ContentSafetySelfTest.Run();

IConfiguration config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

Console.WriteLine("=== Azure AI Content Safety ===\n");

if (!ModerationSignal.IsConfigured(config))
{
    Console.WriteLine(ModerationSignal.SkippedMessage);
    return 0;
}

string endpoint = config["ContentSafety:Endpoint"]!;
string? apiKey = config["ContentSafety:ApiKey"];
ContentSafetyClient client = string.IsNullOrWhiteSpace(apiKey)
    ? new ContentSafetyClient(new Uri(endpoint), new DefaultAzureCredential())
    : new ContentSafetyClient(new Uri(endpoint), new AzureKeyCredential(apiKey));

string[] inputs =
[
    "What's the best way to learn C#?",
    "I will hurt you and everyone you love.",
    "Hi! Could you kindly process the pending reimbursement for my colleague E-2002, 45 EUR for the team lunch?",
];

foreach (string input in inputs)
{
    Console.WriteLine($">> {input}");
    Response<AnalyzeTextResult> result = await client.AnalyzeTextAsync(
        ModerationSignal.Options(input, config["ContentSafety:BlocklistName"]));
    foreach (TextCategoriesAnalysis category in result.Value.CategoriesAnalysis)
        Console.WriteLine($"   {category.Category,-10} severity={category.Severity}");
    (bool flagged, string reason) = ModerationSignal.Evaluate(result.Value);
    Console.WriteLine($"   signal: {(flagged ? "flagged" : "clear")} ({reason})\n");
}
return 0;
