// ============================================================================
// Exercise 01 — Text Processing Workflow (Beginner)
// Workflow with 3 executors: CleanText → CountWords → FormatReport.
// Uses class-based executors (Executor<TInput, TOutput>) from
// Microsoft.Agents.AI.Workflows. FULLY STANDALONE.
// Acceptance check (offline): dotnet run -- --self-test
// ============================================================================

using Microsoft.Agents.AI.Workflows;

Console.WriteLine("=== Exercise: Text Processing Workflow ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// ---------------------------------------------------------------
// Build the workflow
// ---------------------------------------------------------------
// A fresh graph (and fresh executors) for every run.
static Workflow BuildWorkflow()
{
    ExecutorBinding cleanNode = new CleanTextExecutor().BindExecutor();
    ExecutorBinding countNode = new CountWordsExecutor().BindExecutor();
    ExecutorBinding formatNode = new FormatReportExecutor().BindExecutor();

    return new WorkflowBuilder(cleanNode)
        .AddEdge(cleanNode, countNode)
        .AddEdge(countNode, formatNode)
        .WithOutputFrom(formatNode)
        .WithName("TextProcessingWorkflow")
        .WithDescription("Cleans text, counts words, and formats a report")
        .Build();
}

// The report is read from the WorkflowOutputEvent, never from console text.
static async Task<string?> RunOnceAsync(string text, CancellationToken ct)
{
    Run run = await InProcessExecution.RunAsync(BuildWorkflow(), text, sessionId: Guid.NewGuid().ToString(), ct);
    return run.OutgoingEvents.OfType<WorkflowOutputEvent>().Select(o => o.Data?.ToString()).LastOrDefault();
}

Workflow workflow = BuildWorkflow();

Console.WriteLine($"Workflow: {workflow.Name}");
Console.WriteLine($"Graph:\n{WorkflowVisualizer.ToMermaidString(workflow)}\n");

// ---------------------------------------------------------------
// Execute
// ---------------------------------------------------------------
string sampleText = """
    This   is  some    messy   text    with   extra   spaces.
    It has multiple sentences. Some are short. Others are a bit longer and more descriptive.
    The   workflow   should   clean   this   up   nicely!
    """;

if (args.Contains("--self-test"))
{
    string? first = await RunOnceAsync(sampleText, cts.Token);
    string? second = await RunOnceAsync(sampleText, cts.Token);
    bool fromEvent = first?.Contains("=== Text Analysis Report ===") == true;
    bool same = first is not null && first == second;
    Console.WriteLine($"  {(fromEvent ? "PASS" : "FAIL")}  the report comes from the WorkflowOutputEvent");
    Console.WriteLine($"  {(same ? "PASS" : "FAIL")}  a fresh workflow per run gives identical output twice");
    int passed = (fromEvent ? 1 : 0) + (same ? 1 : 0);
    Console.WriteLine($"\nRESULT {passed}/2");
    return passed == 2 ? 0 : 1;
}

Console.WriteLine("--- Input ---");
Console.WriteLine(sampleText);

Run run = await InProcessExecution.RunAsync(
    workflow, sampleText, sessionId: Guid.NewGuid().ToString(), cts.Token);

Console.WriteLine("\n--- Output ---");
foreach (WorkflowEvent evt in run.OutgoingEvents)
{
    if (evt is WorkflowOutputEvent output)
    {
        Console.WriteLine(output.Data);
    }
}

Console.WriteLine("\n=== Exercise complete! ===");
return 0;

// ---------------------------------------------------------------
// Executors
// ---------------------------------------------------------------

/// <summary>Normalizes whitespace and trims the input.</summary>
internal sealed class CleanTextExecutor() : Executor<string, string>("clean")
{
    public override ValueTask<string> HandleAsync(
        string input, IWorkflowContext context, CancellationToken ct)
    {
        string cleaned = System.Text.RegularExpressions.Regex.Replace(input.Trim(), @"\s+", " ");
        Console.WriteLine($"  [CleanText] {input.Length} chars → {cleaned.Length} chars");
        return ValueTask.FromResult(cleaned);
    }
}

/// <summary>Counts words, characters, and sentences; packs them into a stats string.</summary>
internal sealed class CountWordsExecutor() : Executor<string, string>("count")
{
    public override ValueTask<string> HandleAsync(
        string input, IWorkflowContext context, CancellationToken ct)
    {
        string[] words = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int wordCount = words.Length;
        int charCount = input.Length;
        int sentenceCount = input.Split(['.', '!', '?'], StringSplitOptions.RemoveEmptyEntries).Length;

        string stats = $"WORDS:{wordCount}|CHARS:{charCount}|SENTENCES:{sentenceCount}|TEXT:{input}";
        Console.WriteLine($"  [CountWords] {wordCount} words, {charCount} chars, {sentenceCount} sentences");
        return ValueTask.FromResult(stats);
    }
}

/// <summary>Parses the stats string and formats a readable report as the workflow output.</summary>
[YieldsOutput(typeof(string))]
internal sealed class FormatReportExecutor() : Executor<string>("format")
{
    public override async ValueTask HandleAsync(
        string input, IWorkflowContext context, CancellationToken ct)
    {
        Dictionary<string, string> parts = [];
        foreach (string part in input.Split('|'))
        {
            int colonIndex = part.IndexOf(':');
            if (colonIndex > 0)
            {
                parts[part[..colonIndex]] = part[(colonIndex + 1)..];
            }
        }

        string text = parts.GetValueOrDefault("TEXT", "");
        string report = $"""
            === Text Analysis Report ===
            Words:     {parts.GetValueOrDefault("WORDS", "?")}
            Characters:{parts.GetValueOrDefault("CHARS", "?")}
            Sentences: {parts.GetValueOrDefault("SENTENCES", "?")}
            Preview:   {(text.Length > 50 ? text[..50] + "..." : text)}
            ============================
            """;

        Console.WriteLine("  [FormatReport] Report generated");
        await context.YieldOutputAsync(report, ct);
    }
}
