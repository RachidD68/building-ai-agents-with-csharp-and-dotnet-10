// ============================================================================
// Incident diagnosis: which step gave the wrong reimbursement limit?
// ----------------------------------------------------------------------------
// A wrong limit can come from three places: retrieval found nothing (so the
// model looked up the wrong category), the limit tool failed (so the model
// guessed), or the model misquoted a correct tool result. All three end in a
// confident, wrong answer, and with sensitive data off the framework spans
// alone cannot tell two of them apart. Two step spans of our own can:
//   contoso.retrieval      contoso.retrieval.hits (a count, never the text)
//   contoso.answer.check   contoso.answer.grounded + contoso.answer.reason
// The diagnosis reads exported spans only, so it works on a captured file.
//
//   dotnet run                                 the three faults, diagnosed
//   dotnet run -- --capture <fault> <file>     one request, spans to JSONL
//                                              (fault: none, emptyretrieval,
//                                              toolexception, wrongvalue)
//   dotnet run -- --diagnose <file>            name the failing step
//   dotnet run -- --self-test                  offline checks, RESULT n/n
// Everything here is offline: a scripted model, no provider, no network.
// ============================================================================

using Ch19.Observability;

if (args.Contains("--self-test")) return await SelfTest.RunAsync();

if (args is ["--capture", string faultName, string file])
{
    if (!Enum.TryParse(faultName, ignoreCase: true, out Fault fault))
    {
        Console.Error.WriteLine($"Unknown fault '{faultName}'. Use one of: {string.Join(", ", Enum.GetNames<Fault>())}");
        return 2;
    }
    IncidentTelemetry run = await IncidentRun.RunAsync(fault);
    Diagnosis.Save(file, run.Spans);
    Console.WriteLine($"{run.Spans.Count} spans written to {file}");
    return 0;
}

if (args is ["--diagnose", string path])
{
    Verdict verdict = Diagnosis.Diagnose(Diagnosis.Load(path));
    Console.WriteLine($"failing step: {verdict.Step}");
    Console.WriteLine($"evidence:     {verdict.Evidence}");
    return 0;
}

Console.WriteLine("=== Incident diagnosis ===");
Console.WriteLine($"Question: {IncidentRun.Question}\n");
foreach (Fault fault in Enum.GetValues<Fault>())
{
    IncidentTelemetry run = await IncidentRun.RunAsync(fault);
    Verdict verdict = Diagnosis.Diagnose(run.Spans);
    Console.WriteLine($"{fault,-15} answer: {run.Answer}");
    Console.WriteLine($"{"",-15} step:   {verdict.Step} ({verdict.Evidence})");
}
return 0;
