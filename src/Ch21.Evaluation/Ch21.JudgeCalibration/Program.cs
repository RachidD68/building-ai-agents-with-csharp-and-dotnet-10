// ============================================================================
// Judge calibration: measure the judge against reference labels before trusting it
// ----------------------------------------------------------------------------
// A judge's own confidence is not calibration. This project runs an LLM judge
// with a versioned rubric (rubric/faq-support.v1.md) over a calibration split
// whose items carry reference labels, then prints the confusion matrix, the
// agreement, Cohen's kappa and every disagreement with the rubric line each
// side relied on. The disagreements are what the next rubric version answers.
// The judge must not be the model that produced the answers it grades.
//
//   dotnet run                         replay the recorded verdicts in data/ (no model)
//   dotnet run -- --live               run the judge (Ollama, LlmProvider:Judge:Model)
//   dotnet run -- --live --record P    ... and write the verdicts to file P
//   dotnet run -- --self-test          scripted judge replies; RESULT n/n
// ============================================================================

using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Ch21.JudgeCalibration;

string root = AppContext.BaseDirectory;
CalSet set = CalSet.Load(Path.Combine(root, "data", "calibration.v1.json"));
string rubric = File.ReadAllText(Path.Combine(root, "rubric", $"{set.Rubric}.md"));
if (args.Contains("--self-test")) return await SelfTest.RunAsync(set, rubric);

IConfiguration config = new ConfigurationBuilder()
    .SetBasePath(root)
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables()
    .Build();
string endpoint = config["LlmProvider:Ollama:Endpoint"] ?? "http://localhost:11434";
string judgeModel = config["LlmProvider:Judge:Model"] ?? "llama3.2";
CalItem[] items = [.. set.Items.Where(i => i.Split == "calibration")];

Console.WriteLine("=== Judge calibration ===");
Console.WriteLine($"set {set.Version}, rubric {set.Rubric}, {items.Length} calibration items " +
                  $"({items.Count(i => i.Label == "supported")} supported, {items.Count(i => i.Label != "supported")} unsupported)");

VerdictFile run;
if (args.Contains("--live"))
{
    if (Calibration.SelfJudging(judgeModel, items) is string refusal)
    {
        Console.WriteLine($"Refused: {refusal}");
        return 2;
    }
    using IChatClient judge = new OllamaSharp.OllamaApiClient(new Uri(endpoint), judgeModel);
    List<Verdict> verdicts = [];
    try
    {
        foreach (CalItem item in items) verdicts.Add(await Calibration.JudgeAsync(judge, rubric, item));
    }
    catch (HttpRequestException ex)
    {
        Console.WriteLine($"Ollama is not reachable at {endpoint}: {ex.Message}");
        return 2;
    }
    run = new(judgeModel, set.Rubric, set.Version, DateTimeOffset.UtcNow.ToString("u"), verdicts);
    int recordAt = Array.IndexOf(args, "--record");
    if (recordAt >= 0 && recordAt + 1 < args.Length)
    {
        File.WriteAllText(args[recordAt + 1], JsonSerializer.Serialize(run, JsonOptions.Write) + Environment.NewLine);
        Console.WriteLine($"verdicts recorded to {Path.GetFileName(args[recordAt + 1])}");
    }
}
else
{
    string recorded = Path.Combine(root, "data", $"verdicts.{judgeModel}.{set.Rubric}.json");
    if (!File.Exists(recorded))
    {
        Console.WriteLine($"No recorded verdicts at data/{Path.GetFileName(recorded)}. Run with --live --record <path> first.");
        return 2;
    }
    run = JsonSerializer.Deserialize<VerdictFile>(File.ReadAllText(recorded), JsonOptions.Read)!;
    // Re-parse the raw replies, so a parser fix applies to what was recorded.
    run = run with { Verdicts = [.. run.Verdicts.Select(v => Calibration.Parse(v.ItemId, v.Raw))] };
    Console.WriteLine($"replaying {run.Verdicts.Count} verdicts recorded {run.RecordedUtc} from judge {run.JudgeModel}");
}

Console.WriteLine($"judge {run.JudgeModel}; answers produced by: {string.Join(", ", items.Select(i => i.ProducedBy).Distinct())}");
Console.WriteLine();
CalibrationReport report = Calibration.Compare(items, run.Verdicts);
Calibration.Print(report, items.Length);
Console.WriteLine();
Console.WriteLine($"{items.Length} items, one judge run at temperature 0: an estimate for this set and this rubric version only.");
return 0;
