// ============================================================================
// Approval Workflow: the Contoso claim-review graph with a person in the loop
// ----------------------------------------------------------------------------
// One expense claim goes through the graph in ClaimReview.cs: classify, a
// switch, two checks in parallel, a barrier, a summary written by an agent,
// then a RequestPort that waits for a typed ReviewReply.
//
//   approve -> "approved" (the only outcome that approves anything)
//   revise  -> the writer rewrites with your note, at most twice
//   reject  -> "rejected"
//   anything else, an empty line or end of input -> the review stays pending
//
// Modes:
//   dotnet run                 live: the model writes the summary, you decide
//   dotnet run -- --offline    a scripted model, no provider needed
//   dotnet run -- --self-test  offline checks of the parser and the graph
//
// Runs against Ollama (llama3.2) by default; see appsettings.json.
// ============================================================================

using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgenticAI.Common.Extensions;
using Ch14.ApprovalWorkflow;

if (args.Contains("--self-test")) return await SelfTest.RunAsync();

using IHost? host = args.Contains("--offline") ? null : BuildHost(args);
IChatClient chatClient = host?.Services.GetRequiredService<IChatClient>() ?? new ScriptedChatClient();

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine("=== Approval Workflow: Contoso claim review ===\n");

ExpenseClaim claim = new("CLM-1042", "lodging", 340m, HasReceipt: true,
    "Two hotel nights for the partner summit; the venue hotel was full.");
Console.WriteLine($"[CLAIM] {claim.Id}: {claim.Category}, {claim.Amount:0.00}, receipt: {claim.HasReceipt}\n");

Workflow workflow = ClaimReviewGraph.Build(chatClient, maxRevisions: 2);   // one fresh graph per run
await using StreamingRun run = await InProcessExecution.RunStreamingAsync(
    workflow, claim, sessionId: Guid.NewGuid().ToString(), cts.Token);

ClaimOutcome? outcome = null;
bool pending = false;
await foreach (WorkflowEvent evt in run.WatchStreamAsync(cts.Token))
{
    if (evt is RequestInfoEvent info && info.Request.TryGetDataAs(out ReviewRequest? request))
    {
        Console.WriteLine(new string('=', 60));
        Console.WriteLine($"REVIEW REQUIRED (revision {request.Revision})");
        foreach (CheckResult check in request.Checks)
            Console.WriteLine($"  {check.Check,-8} {(check.Passed ? "pass" : "FAIL")}  {check.Detail}");
        Console.WriteLine($"\n{request.Summary}\n");

        // Only a typed decision moves the graph. No decision: send nothing.
        ReviewReply? reply = ReadReply();
        if (reply is null) { pending = true; break; }
        await run.SendResponseAsync(info.Request.CreateResponse(reply));
        Console.WriteLine($"  Decision sent: {reply.Decision}\n");
    }
    else if (evt is WorkflowOutputEvent { Data: ClaimOutcome done })
    {
        outcome = done;
    }
    else if (evt is WorkflowErrorEvent error)
    {
        Console.WriteLine($"  [ERROR] {error.Data}");
    }
}

if (pending || outcome is null)
{
    Console.WriteLine($"\n  Review left pending ({await run.GetStatusAsync()}). Nothing was approved.");
    return 2;
}

Console.WriteLine($"OUTCOME {outcome.ClaimId}: {outcome.Status}. {outcome.Detail}");
return outcome.Status == "approved" ? 0 : 2;

// Asks up to three times; null on end of input or three entries that are not decisions.
static ReviewReply? ReadReply()
{
    for (int attempt = 0; attempt < 3; attempt++)
    {
        Console.WriteLine("Enter 'approve', 'revise' or 'reject':");
        Console.Write("> ");
        string? line = Console.ReadLine();
        if (line is null) return null;                       // end of input
        if (ReviewDecisions.TryParse(line, out ReviewDecision decision))
        {
            if (decision != ReviewDecision.Revise) return new ReviewReply(decision);
            Console.Write("Note for the writer: ");
            return new ReviewReply(decision, Console.ReadLine()?.Trim() ?? "");
        }
        Console.WriteLine($"  '{line.Trim()}' is not a decision.");
    }
    return null;
}

static IHost BuildHost(string[] args)
{
    HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddLlmChatClient(builder.Configuration);
    return builder.Build();
}

/// <summary>Offline checks: dotnet run -- --self-test</summary>
internal static class SelfTest
{
    public static async Task<int> RunAsync()
    {
        int passed = 0, total = 0;
        void Check(string name, bool ok)
        {
            total++; passed += ok ? 1 : 0;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
        }

        Console.WriteLine("=== Approval Workflow self-test (offline) ===\n");
        (string? Input, ReviewDecision? Expected)[] cases =
        [
            ("approve", ReviewDecision.Approve), ("  APPROVE ", ReviewDecision.Approve),
            ("revise", ReviewDecision.Revise), ("reject", ReviewDecision.Reject),
            ("", null), ("   ", null), ("aprove", null), ("approved", null), ("yes", null), (null, null),
        ];
        foreach ((string? input, ReviewDecision? expected) in cases)
        {
            ReviewDecision? actual = ReviewDecisions.TryParse(input, out ReviewDecision d) ? d : null;
            string shown = input is null ? "<end of input>" : $"\"{input}\"";
            Check($"{shown,-16} -> {actual?.ToString() ?? "pending"}", actual == expected);
        }

        ExpenseClaim claim = new("CLM-T1", "meals", 40m, HasReceipt: true, "Team lunch");
        (ClaimOutcome? approved, _) = await RunOnceAsync(claim, new ReviewReply(ReviewDecision.Approve));
        Check("graph: approve -> approved", approved?.Status == "approved");
        (ClaimOutcome? none, RunStatus status) = await RunOnceAsync(claim, reply: null);
        Check("graph: no reply -> no outcome, PendingRequests", none is null && status == RunStatus.PendingRequests);

        Console.WriteLine($"\nRESULT {passed}/{total}");
        return passed == total ? 0 : 1;
    }

    private static async Task<(ClaimOutcome?, RunStatus)> RunOnceAsync(ExpenseClaim claim, ReviewReply? reply)
    {
        Workflow workflow = ClaimReviewGraph.Build(new ScriptedChatClient());
        await using StreamingRun run = await InProcessExecution.RunStreamingAsync(workflow, claim, sessionId: Guid.NewGuid().ToString());
        ClaimOutcome? outcome = null;
        ExternalRequest? asked;
        do
        {
            asked = null;
            await foreach (WorkflowEvent evt in run.WatchStreamAsync(blockOnPendingRequest: false))
            {
                if (evt is RequestInfoEvent info) asked = info.Request;
                if (evt is WorkflowOutputEvent { Data: ClaimOutcome done }) outcome = done;
            }
            if (asked is not null && reply is not null) await run.SendResponseAsync(asked.CreateResponse(reply));
        } while (asked is not null && reply is not null);
        return (outcome, await run.GetStatusAsync());
    }
}
