// ============================================================================
// Workflow Tests: one case per branch, no model
// ----------------------------------------------------------------------------
// Build the graph, run it in-process with inputs that reach each branch, and
// assert on the streamed events and the outcome. The claim-review graph is the
// one Ch14.ApprovalWorkflow runs (its ClaimReview.cs is compiled into this
// project); a scripted IChatClient stands in for the model and a scripted
// reviewer stands in for the person.
//
// "DEFECT REPRODUCED" lines show a failure the chapter fixes: the check passes
// when the broken behaviour really happens.
//
// A plain console harness rather than an xUnit project: move each case into a
// [Fact] and Check(...) into Assert to port it. Exit code 0 = every check passed.
// Accepts --self-test (same run) so the repository's self-test sweep finds it.
// ============================================================================

using System.Text.Json;
using Ch14.ApprovalWorkflow;
using Ch14.WorkflowTests;
using Microsoft.Agents.AI.Workflows;

_ = args.Contains("--self-test");   // the harness always runs everything offline
Console.WriteLine("=== Workflow Tests (offline) ===\n");

ExpenseClaim meals = new("CLM-T1", "meals", 40m, HasReceipt: true, "Team lunch");

Console.WriteLine("-- Sentiment router: one case per branch");
await Sentiment.CaseAsync("positive routes to thank-you", "I love this, it's great!", "thank-you", "Thank you");
await Sentiment.CaseAsync("negative routes to escalation", "This is terrible and broken.", "escalation", "ESCALATION");
await Sentiment.CaseAsync("neutral routes to acknowledgement", "It arrived on Tuesday.", "neutral", "Acknowledged");

Console.WriteLine("\n-- Claim review: every branch");
ReviewRun approve = await Review.RunAsync(meals, _ => new ReviewReply(ReviewDecision.Approve));
Harness.Check("approve -> approved, one review request",
    approve.Outcome?.Status == "approved" && approve.Requests.Count == 1);
Harness.Check("both checks ran in one superstep and met at the barrier",
    approve.Steps.TryGetValue("policy-check", out int policyStep) && approve.Steps.TryGetValue("receipt-check", out int receiptStep) &&
    policyStep == receiptStep && approve.Steps["collect"] > policyStep &&
    approve.Requests[0].Checks.Select(c => c.Check).SequenceEqual(["policy", "receipt"]),
    string.Join(",", approve.Steps.Select(s => $"{s.Key}@{s.Value}")));

ReviewRun revise = await Review.RunAsync(meals, r => r.Revision == 0
    ? new ReviewReply(ReviewDecision.Revise, "Say who attended.") : new ReviewReply(ReviewDecision.Approve));
Harness.Check("revise then approve -> approved, revision 1 rewritten by the agent",
    revise.Outcome?.Status == "approved" && revise.Requests.Count == 2 && revise.Requests[1].Revision == 1 &&
    revise.Requests[0].Summary != revise.Requests[1].Summary);

ReviewRun reject = await Review.RunAsync(meals, _ => new ReviewReply(ReviewDecision.Reject, "Personal expense."));
Harness.Check("reject -> rejected, distinct from revise", reject.Outcome?.Status == "rejected" && reject.Requests.Count == 1);

ReviewRun returned = await Review.RunAsync(meals with { Category = "gym" }, _ => new ReviewReply(ReviewDecision.Approve));
Harness.Check("uncovered category -> switch default -> returned, nobody asked",
    returned.Outcome?.Status == "returned" && returned.Requests.Count == 0);

foreach ((string label, string? line) in new[] { ("empty line", ""), ("end of input", null), ("unknown word \"approved\"", "approved") })
{
    ReviewRun run = await Review.RunAsync(meals, _ => ReviewDecisions.TryParse(line, out ReviewDecision d) ? new ReviewReply(d) : null);
    Harness.Check($"{label} -> no reply sent, run PendingRequests, no outcome",
        run.Outcome is null && run.Status == RunStatus.PendingRequests);
}

ReviewRun capped = await Review.RunAsync(meals, _ => new ReviewReply(ReviewDecision.Revise), maxRevisions: 2);
Harness.Check("always revise, cap 2 -> not-approved after 3 requests (cap is not success)",
    capped.Outcome?.Status == "not-approved" && capped.Requests.Count == 3);

(string? refused, RunStatus afterRefusal, ClaimOutcome? typedAfter) = await Review.SendWrongTypeAsync(meals);
Harness.Check("typed port refuses a string reply; that run stays pending and still takes a typed approve",
    refused?.Contains("does not match expected response type") == true && afterRefusal == RunStatus.PendingRequests &&
    typedAfter?.Status == "approved", $"status={afterRefusal} typed={typedAfter?.Status}");

ReviewDecision fromJson = JsonSerializer.Deserialize<ReviewReply>("{}")!.Decision;
ReviewRun noDecision = await Review.RunAsync(meals, _ => new ReviewReply(default));
ReviewRun emptyJson = await Review.RunAsync(meals, _ => JsonSerializer.Deserialize<ReviewReply>("{\"Note\":\"pls check\"}"));
ReviewRun outOfRange = await Review.RunAsync(meals, _ => new ReviewReply((ReviewDecision)7));
Harness.Check("default(ReviewDecision), JSON {} and decision 7 are not Approve -> switch default -> not-approved",
    fromJson != ReviewDecision.Approve && !ReviewDecisions.TryParse("garbage", out ReviewDecision junk) && junk != ReviewDecision.Approve &&
    new[] { noDecision, emptyJson, outOfRange }.All(r => r.Outcome?.Status == "not-approved" && r.Errors.Count == 0),
    $"{noDecision.Outcome?.Status},{emptyJson.Outcome?.Status},{outOfRange.Outcome?.Status}");

var probe = await NullProbe.RunAsync(meals);
Harness.Check("switch after the RequestPort is called with null; switch after classify is not",
    probe.PortNulls > 0 && probe.PlainNulls == 0, $"port={probe.PortNulls} plain={probe.PlainNulls}");
Harness.Defect("port switch written r => r.Decision == ...: NullReferenceException, no outcome",
    probe.UnsafeOutcome is null && probe.UnsafeErrors.Any(e => e.Contains("NullReferenceException")));

Console.WriteLine("\n-- Where it fails, reproduced");
string[] v1Inputs = ["", "aprove", "reject"];
Harness.Defect("(a) v1 rule: empty line, typo, \"reject\" and end of input all became approve",
    v1Inputs.All(i => Review.V1Rule(i) == "approve") && Review.V1Rule(null) == "approve");
Harness.Check("(a) fixed: empty, typo and end of input are no decision; \"reject\" is Reject",
    !ReviewDecisions.TryParse("", out _) && !ReviewDecisions.TryParse("aprove", out _) && !ReviewDecisions.TryParse(null, out _) &&
    ReviewDecisions.TryParse("reject", out ReviewDecision rejected) && rejected == ReviewDecision.Reject);

(string? duplicate, RunStatus afterDuplicate, ClaimOutcome? duplicateOutcome) = await Review.SendTwiceAsync(meals);
Harness.Check("(b) the same reply sent twice: no pending request for that id; the run ends with no outcome",
    duplicate?.Contains("No pending request with ID") == true && afterDuplicate == RunStatus.Ended && duplicateOutcome is null,
    $"status={afterDuplicate} outcome={duplicateOutcome?.Status}");

Harness.Defect("(c) one graph with a stateful executor, run twice: run 2 sees run 1's state",
    await Reuse.SharedCounterAsync() is ("seen 1", "seen 2"));
Harness.Check("(c) fixed: a fresh graph per run gives the same output twice",
    await Reuse.FreshCounterAsync() is ("seen 1", "seen 1"));
Harness.Check("(c) fixed: IResettableExecutor resets between runs of one graph",
    await Reuse.ResettableCounterAsync() is ("seen 1", "seen 1"));
string? owned = await Reuse.SecondRunWhileOwnedAsync(meals);
Harness.Check("(c) a second run is refused while run 1 owns the graph (paused at the port)",
    owned?.Contains("already owned by another runner") == true);
int exactlyOne = await Reuse.TwoRunsAtOnceAsync(meals, rounds: 20);
Harness.Check("(c) two runs started at once behind a barrier: exactly one refused, 20 of 20 rounds",
    exactlyOne == 20, $"{exactlyOne}/20");

ReviewRun unbounded = await Review.RunAsync(meals, _ => new ReviewReply(ReviewDecision.Revise), maxRevisions: int.MaxValue, maxRounds: 10);
Harness.Defect("(d) no cap, a reviewer who always says revise: 10 rounds, still no outcome",
    unbounded.Outcome is null && unbounded.Requests.Count == 10);

Console.WriteLine("\n-- Declarative workflow (YAML)");
await DeclarativeCases.RunAllAsync();

return Harness.Finish();
