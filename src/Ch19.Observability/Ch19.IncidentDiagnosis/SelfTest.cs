namespace Ch19.Observability;

/// <summary>
/// dotnet run -- --self-test. Three injected faults (empty retrieval, tool
/// exception, wrong amount in the answer), each captured to a file and
/// diagnosed from that file alone; then the same faults with framework spans
/// only; then a search of everything exported for the seeded confidential text.
/// </summary>
internal static class SelfTest
{
    // Strings seeded into the prompt, the policy passages and the tool's exception message.
    private static readonly string[] Seeded =
        ["Dana Whitfield", "CONF-2208", "R. Osei", "CONF-3119", "M. Diallo", "limits service did not answer"];

    private static int s_passed, s_total;

    public static async Task<int> RunAsync()
    {
        Console.WriteLine("=== Incident diagnosis: self-test (offline) ===");
        string dir = Directory.CreateTempSubdirectory("ch19-incident-").FullName;
        List<IncidentTelemetry> everything = [];
        try
        {
            Console.WriteLine("\n-- One request per fault, captured to a file, diagnosed from the file --");
            (Fault Fault, string Step)[] cases =
            [
                (Fault.None, Diagnosis.None),
                (Fault.EmptyRetrieval, Diagnosis.Retrieval),
                (Fault.ToolException, Diagnosis.Tool),
                (Fault.WrongValue, Diagnosis.Model),
            ];
            foreach ((Fault fault, string step) in cases)
            {
                IncidentTelemetry run = await IncidentRun.RunAsync(fault);
                everything.Add(run);
                string file = Path.Combine(dir, $"{fault}.jsonl");
                Diagnosis.Save(file, run.Spans);
                Verdict verdict = Diagnosis.Diagnose(Diagnosis.Load(file));
                Console.WriteLine($"    {fault,-15} answer: \"{run.Answer}\"");
                Expect($"{fault}: diagnosed as {verdict.Step} ({verdict.Evidence})", verdict.Step == step);
            }

            // A converter from a backend export may spell the string "false" rather than "False".
            string wrong = File.ReadAllText(Path.Combine(dir, $"{Fault.WrongValue}.jsonl"));
            string lowered = Path.Combine(dir, "wrongvalue-lowercase.jsonl");
            File.WriteAllText(lowered, wrong.Replace("\"contoso.answer.grounded\":\"False\"", "\"contoso.answer.grounded\":\"false\""));
            Verdict spelled = Diagnosis.Diagnose(Diagnosis.Load(lowered));
            Expect($"WrongValue with the flag written \"false\": diagnosed as {spelled.Step} (the reason code decides, not the spelling)",
                spelled.Step == Diagnosis.Model && File.ReadAllText(lowered).Contains("\"contoso.answer.grounded\":\"false\""));

            Console.WriteLine("\n-- The same faults with framework spans only (no step spans) --");
            List<string> undetermined = [];
            foreach (Fault fault in new[] { Fault.EmptyRetrieval, Fault.ToolException, Fault.WrongValue })
            {
                IncidentTelemetry run = await IncidentRun.RunAsync(fault, IncidentRun.FrameworkSourcesOnly);
                everything.Add(run);
                Verdict verdict = Diagnosis.Diagnose(run.Spans);
                Console.WriteLine($"    {fault,-15} -> {verdict.Step} ({verdict.Evidence})");
                if (verdict.Step == Diagnosis.Unknown) undetermined.Add(fault.ToString());
            }
            Expect($"DEFECT REPRODUCED: framework spans alone leave {undetermined.Count} of 3 faults undetermined " +
                   $"({string.Join(", ", undetermined)}): both look like a successful run",
                undetermined.SequenceEqual([nameof(Fault.EmptyRetrieval), nameof(Fault.WrongValue)]));

            Console.WriteLine("\n-- Confidential text in what was exported --");
            IncidentTelemetry unredacted = await IncidentRun.RunAsync(Fault.ToolException, redact: false);
            SpanRecord? described = unredacted.Spans.FirstOrDefault(s => s.StatusDescription is { Length: > 0 });
            Expect($"DEFECT REPRODUCED: without the redactor, {described?.Name} exports the exception message " +
                   $"as its status description (\"{described?.StatusDescription}\"), sensitive data off",
                described?.StatusDescription == ReimbursementPipeline.ToolFailureMessage);
            string[] lines = [.. everything.SelectMany(r => TelemetryText.Of(r.Spans, r.Points, r.Logs))];
            string[] found = [.. Seeded.Where(s => lines.Any(l => l.Contains(s, StringComparison.Ordinal)))];
            Expect($"none of the {Seeded.Length} seeded strings appears in {lines.Length} exported values " +
                   $"(spans, metric tags, logs) over {everything.Count} runs",
                found.Length == 0 && lines.Length > 0);
            foreach (string s in found) Console.WriteLine($"      found: {s}");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }

        Console.WriteLine($"\nRESULT {s_passed}/{s_total}");
        return s_passed == s_total ? 0 : 1;
    }

    private static void Expect(string name, bool ok)
    {
        s_total++;
        if (ok) s_passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    }
}
