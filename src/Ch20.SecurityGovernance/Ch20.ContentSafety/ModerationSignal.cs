using Azure.AI.ContentSafety;
using Microsoft.Extensions.Configuration;

namespace Ch20.ContentSafety;

/// <summary>Turns a Content Safety result into one signal: flagged or clear, with the reason.</summary>
public static class ModerationSignal
{
    /// <summary>Severity at or above this value flags the text (default output type: "FourSeverityLevels").</summary>
    public const int Threshold = 4;

    public const string SkippedMessage =
        "[skipped] Azure AI Content Safety is not configured (ContentSafety:Endpoint is empty).\n" +
        "Set ContentSafety:Endpoint and sign in for DefaultAzureCredential, or set ContentSafety:ApiKey.";

    public static bool IsConfigured(IConfiguration config) => !string.IsNullOrWhiteSpace(config["ContentSafety:Endpoint"]);

    public static AnalyzeTextOptions Options(string text, string? blocklistName)
    {
        AnalyzeTextOptions options = new(text);
        if (!string.IsNullOrWhiteSpace(blocklistName))
        {
            options.BlocklistNames.Add(blocklistName);
            options.HaltOnBlocklistHit = false;
        }
        return options;
    }

    public static (bool Flagged, string Reason) Evaluate(AnalyzeTextResult result)
    {
        if (result.BlocklistsMatch is { Count: > 0 } hits)
            return (true, $"blocklist:{hits[0].BlocklistName}");
        foreach (TextCategoriesAnalysis c in result.CategoriesAnalysis)
        {
            if (c.Severity is null) return (true, $"{c.Category}:severity_missing");   // unknown is not clear
            if (c.Severity >= Threshold) return (true, $"{c.Category}:{c.Severity}");
        }
        return (false, "below_threshold");
    }
}

internal static class ContentSafetySelfTest
{
    public static int Run()
    {
        Console.WriteLine("=== Azure AI Content Safety: self-test (offline, no Azure call) ===\n");
        int passed = 0, total = 0;
        void Expect(string name, bool ok)
        {
            total++;
            if (ok) passed++;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
        }

        IConfiguration empty = new ConfigurationBuilder().Build();
        Expect("no endpoint: not configured, the live run skips", !ModerationSignal.IsConfigured(empty));

        AnalyzeTextOptions options = ModerationSignal.Options("text", "contoso-terms");
        Expect("a blocklist name is sent and does not halt the analysis",
            options.BlocklistNames.Contains("contoso-terms") && options.HaltOnBlocklistHit == false);

        static AnalyzeTextResult Result(int? violence, params TextBlocklistMatch[] hits) =>
            ContentSafetyModelFactory.AnalyzeTextResult(hits,
            [
                ContentSafetyModelFactory.TextCategoriesAnalysis(TextCategory.Hate, 0),
                ContentSafetyModelFactory.TextCategoriesAnalysis(TextCategory.Violence, violence),
            ]);

        Expect("severity 2 -> clear", ModerationSignal.Evaluate(Result(2)) == (false, "below_threshold"));
        Expect("severity 4 -> flagged Violence:4", ModerationSignal.Evaluate(Result(4)) == (true, "Violence:4"));
        Expect("missing severity -> flagged, not clear", ModerationSignal.Evaluate(Result(null)).Flagged);
        Expect("blocklist hit -> flagged",
            ModerationSignal.Evaluate(Result(0, ContentSafetyModelFactory.TextBlocklistMatch("contoso-terms", "1", "E-2002"))) == (true, "blocklist:contoso-terms"));

        Console.WriteLine($"\nRESULT {passed}/{total}");
        Console.WriteLine("(The Azure path is compiled but was not run here: no Content Safety resource is configured.)");
        return passed == total ? 0 : 1;
    }
}
