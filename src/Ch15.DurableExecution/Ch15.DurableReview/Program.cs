// ============================================================================
// Durable Review: a claim review that survives a process stop
// ----------------------------------------------------------------------------
// Ch14.CheckpointedApproval showed the mechanism. Here the three steps are
// three processes that share only durable state:
//
//   prepare   writes the claim (version 1), runs the graph to the review
//             port, stores the checkpoint pointer and the graph version, exits
//   review    a reviewer records a typed decision bound to the claim version;
//             no workflow runs, nothing waits in memory
//   resume    a new process checks the graph version, revalidates the
//             decision against the records as they are NOW (version, expiry,
//             reviewer role), then restores the checkpoint and finishes
//
// State on disk under --store: checkpoints/ (FileSystemJsonCheckpointStore)
// and app.db (SQLite: claims, roles, decisions, sessions).
//
//   dotnet run -- prepare [--claim CLM-1001] [--requester alice] [--amount 64.80]
//   dotnet run -- review <claim> <reviewer> <approve|reject>
//   dotnet run -- resume <claim>
//   dotnet run -- amend <claim> <amount>      the requester edits the claim
//   dotnet run -- revoke <subject>            the identity system drops a role
//   dotnet run -- --self-test                 every verb as a child process, offline
// Options: --store <folder>, --offline (scripted model), --now <ISO time>,
//   --graph next / --rules next (an upgraded build), --skip-version-check
//   (shows what MAF alone catches). Exit codes: 0 done, 2 usage, 3 failed,
//   4 blocked (nothing finalized), 5 still waiting for a decision.
//
// prepare runs against Ollama (llama3.2) by default; see appsettings.json.
// review and resume never call a model.
// ============================================================================

using System.Globalization;
using Ch15.DurableReview;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgenticAI.Common.Extensions;

if (args.Contains("--self-test")) return await DurableReviewSelfTest.RunAsync();

string[] valued = ["--store", "--claim", "--requester", "--amount", "--currency", "--now", "--graph", "--rules"];
string[] words = Positional();
string verb = words.FirstOrDefault() ?? "";
string storePath = Option("--store") ?? Path.Combine(Path.GetTempPath(), "AgenticAI", "Ch15.DurableReview");
DateTimeOffset now = Option("--now") is { } at
    ? DateTimeOffset.Parse(at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
    : DateTimeOffset.UtcNow;
string rules = Option("--rules") == "next" ? ReviewGraph.NextRulesVersion : ReviewGraph.RulesVersion;
bool nextGraph = Option("--graph") == "next";

Directory.CreateDirectory(storePath);
string checkpointsPath = Path.Combine(storePath, "checkpoints");
ReviewStore store = new(Path.Combine(storePath, "app.db"));

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

return verb switch
{
    "prepare" => await PrepareAsync(),
    "review" when words.Length >= 4 => Review(words[1], words[2], words[3]),
    "resume" when words.Length >= 2 => await ResumeAsync(words[1]),
    "amend" when words.Length >= 3 => Amend(words[1], words[2]),
    "revoke" when words.Length >= 2 => Revoke(words[1]),
    _ => Usage(),
};

// ---------------------------------------------------------------
// prepare: process 1. The claim, the summary, the paused review.
// ---------------------------------------------------------------
async Task<int> PrepareAsync()
{
    Console.WriteLine("=== Durable Review: prepare ===\n");
    store.SeedRoles();
    ClaimRow claim = store.CreateOrLoadClaim(Option("--claim") ?? "CLM-1001", Option("--requester") ?? "alice",
        decimal.Parse(Option("--amount") ?? "64.80", CultureInfo.InvariantCulture), Option("--currency") ?? "EUR");
    if (claim.State != "AwaitingReview")
        return Fail(3, $"claim {claim.ClaimId} is already {claim.State}.");

    using IHost? host = args.Contains("--offline") ? null : BuildHost();
    IChatClient model = host?.Services.GetRequiredService<IChatClient>() ?? new ScriptedChatClient();
    Workflow workflow = ReviewGraph.Build(model, rules, nextGraph);
    string graph = GraphVersion.Of(workflow, rules);

    Directory.CreateDirectory(checkpointsPath);
    using FileSystemJsonCheckpointStore files = new(new DirectoryInfo(checkpointsPath));
    CheckpointManager checkpoints = CheckpointManager.CreateJson(files);
    string sessionId = Guid.NewGuid().ToString("N");

    CheckpointInfo? paused = null;
    bool requested = false;
    await using (StreamingRun run = await InProcessExecution.RunStreamingAsync(workflow,
        new ClaimTicket(claim.ClaimId, claim.Version, claim.Requester, claim.Amount, claim.Currency),
        checkpoints, sessionId, cts.Token))
    {
        await foreach (WorkflowEvent evt in run.WatchStreamAsync(cts.Token))
        {
            if (evt is RequestInfoEvent request && request.Request.TryGetDataAs(out ReviewRequest? asked) && asked is not null)
            {
                Console.WriteLine($"Review requested for {asked.ClaimId} v{asked.ClaimVersion}: " +
                                  $"{asked.Amount:0.00} {asked.Currency} from {asked.Requester}");
                Console.WriteLine($"Summary: {asked.Summary}\n");
                requested = true;
            }
            // The superstep that raised the request ends with the checkpoint that holds it.
            if (evt is SuperStepCompletedEvent step && step.CompletionInfo?.Checkpoint is { } cp)
            {
                paused = cp;
                if (requested) break;
            }
        }
    }
    if (!requested || paused is null)
        return Fail(3, "the workflow never reached the review port.");

    // The pointer the next process needs, in the application's own store.
    store.SaveSession(new SessionRow(claim.ClaimId, claim.Version, paused.SessionId, paused.CheckpointId, graph));
    Console.WriteLine($"Claim:      {claim.ClaimId} v{claim.Version}");
    Console.WriteLine($"Session:    {paused.SessionId}");
    Console.WriteLine($"Checkpoint: {paused.CheckpointId}");
    Console.WriteLine($"Graph:      {graph}");
    Console.WriteLine($"\nThis process now exits. Next: review {claim.ClaimId} rita approve, then resume {claim.ClaimId}");
    return 0;
}

// ---------------------------------------------------------------
// review: any process, any time. Writes one row; runs no workflow.
// ---------------------------------------------------------------
int Review(string claimId, string reviewer, string word)
{
    Console.WriteLine("=== Durable Review: review ===\n");
    ReviewDecision decision;
    switch (word.Trim().ToLowerInvariant())
    {
        case "approve": decision = ReviewDecision.Approve; break;
        case "reject": decision = ReviewDecision.Reject; break;
        default: return Fail(2, $"unknown decision \"{word}\" (use approve or reject).");
    }
    string? refused = store.RecordDecision(claimId, reviewer, decision, now);
    if (refused is not null)
    {
        Console.WriteLine($"REFUSED {refused}: nothing recorded.");
        return 4;
    }
    DecisionRow d = store.LatestDecision(claimId)!;
    Console.WriteLine($"Decision recorded: {d.Decision.ToString().ToLowerInvariant()} by {d.ReviewerId} " +
                      $"for {claimId} v{d.ClaimVersion}, valid until {d.ExpiresAt:yyyy-MM-dd HH:mm}Z");
    return 0;
}

// ---------------------------------------------------------------
// resume: process 3. Check, then restore, then finish.
// ---------------------------------------------------------------
async Task<int> ResumeAsync(string claimId)
{
    Console.WriteLine("=== Durable Review: resume ===\n");
    ClaimRow? claim = store.LoadClaim(claimId);
    SessionRow? session = store.LoadSession(claimId);
    if (claim is null || session is null)
        return Fail(3, $"no paused review for claim {claimId} in {storePath}.");
    if (claim.State != "AwaitingReview")
        return Fail(3, $"claim {claimId} is already {claim.State}; there is nothing to resume.");

    // 1. The graph that paused the review must be the graph that resumes it.
    //    The model is never called on resume: the summary is in the checkpoint.
    Workflow workflow = ReviewGraph.Build(new NoModelChatClient(), rules, nextGraph);
    string running = GraphVersion.Of(workflow, rules);
    Console.WriteLine($"Graph: paused by {session.GraphVersion}, this build runs {running}");
    if (session.GraphVersion != running && !args.Contains("--skip-version-check"))
        return Blocked("graph_version_mismatch",
            "migrate the pending review explicitly, or finish it with the build that paused it");

    // 2. The decision, checked against the records as they are now.
    (ReviewVerdict? verdict, string? blocked) = store.Revalidate(claim, now);
    if (blocked == "no_decision")
    {
        Console.WriteLine($"WAITING: no decision recorded for {claimId} v{claim.Version}. Nothing was resumed.");
        return 5;
    }
    DecisionRow? d = store.LatestDecision(claimId);
    if (blocked is not null)
        return Blocked(blocked, blocked switch
        {
            "stale_approval" => $"the decision covers v{d!.ClaimVersion}; the claim is now v{claim.Version}",
            "approval_expired" => $"the decision expired at {d!.ExpiresAt:yyyy-MM-dd HH:mm}Z",
            "reviewer_revoked" => $"{d!.ReviewerId} no longer holds {ReviewStore.ReviewerRole}",
            _ => blocked,
        });
    if (session.ClaimVersion != verdict!.ClaimVersion)
        return Blocked("stale_checkpoint",
            $"the paused review asked about v{session.ClaimVersion}; run prepare again for v{claim.Version}");
    Console.WriteLine($"Decision: {verdict.Decision.ToString().ToLowerInvariant()} by {verdict.ReviewerId} " +
                      $"for v{verdict.ClaimVersion}, still valid\n");

    // 3. Restore the checkpoint in this process and answer the re-emitted request.
    ReviewOutcome? outcome = null;
    try
    {
        using FileSystemJsonCheckpointStore files = new(new DirectoryInfo(checkpointsPath));
        CheckpointManager checkpoints = CheckpointManager.CreateJson(files);
        CheckpointInfo from = new(session.SessionId, session.CheckpointId);
        await using StreamingRun run = await InProcessExecution.ResumeStreamingAsync(workflow, from, checkpoints, cts.Token);

        using CancellationTokenSource watchdog = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        watchdog.CancelAfter(TimeSpan.FromSeconds(30));
        await foreach (WorkflowEvent evt in run.WatchStreamAsync(watchdog.Token))
        {
            if (evt is RequestInfoEvent request && request.Request.TryGetDataAs(out ReviewRequest? asked) && asked is not null)
            {
                Console.WriteLine($"Review request re-emitted in a new process: {asked.ClaimId} v{asked.ClaimVersion}");
                await run.SendResponseAsync(request.Request.CreateResponse(verdict));
            }
            if (evt is WorkflowOutputEvent { Data: ReviewOutcome o }) { outcome = o; break; }
            if (evt is WorkflowErrorEvent failure)
                return Fail(3, $"the resumed review failed: {failure.Exception?.Message}");
        }
    }
    catch (InvalidDataException ex)
    {
        // What MAF refuses on its own: a checkpoint from a graph of another shape.
        return Blocked("checkpoint_incompatible", ex.Message);
    }
    catch (OperationCanceledException) when (!cts.IsCancellationRequested)
    {
        return Fail(3, $"checkpoint {session.CheckpointId} re-emitted no review request.");
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                   or KeyNotFoundException or InvalidOperationException)
    {
        return Fail(3, $"checkpoint {session.CheckpointId} is missing, busy or unreadable ({ex.GetType().Name}: {ex.Message})");
    }

    if (outcome is null) return Fail(3, "the resumed review produced no outcome.");
    if (outcome.Status is not ("approved" or "rejected"))
        return Blocked(outcome.Status, "the verdict did not match the request in the checkpoint");
    // One outcome per claim version, even if two resumes race to this line.
    if (!store.Finish(claimId, outcome.ClaimVersion, outcome.Status == "approved" ? "Approved" : "Rejected"))
        return Fail(3, $"claim {claimId} was finished by another process first.");
    Console.WriteLine($"[{outcome.Status.ToUpperInvariant()}] {outcome.ClaimId} v{outcome.ClaimVersion} " +
                      $"by {outcome.ReviewerId} (rules {outcome.RulesVersion})");
    return 0;
}

int Amend(string claimId, string amount)
{
    bool ok = store.Amend(claimId, decimal.Parse(amount, CultureInfo.InvariantCulture));
    ClaimRow? claim = store.LoadClaim(claimId);
    Console.WriteLine(ok ? $"Amended: {claimId} is now v{claim!.Version} at {claim.Amount:0.00} {claim.Currency}"
                         : $"REFUSED: {claimId} is not waiting for review.");
    return ok ? 0 : 4;
}

int Revoke(string subject)
{
    store.Revoke(subject, ReviewStore.ReviewerRole);
    Console.WriteLine($"Revoked: {subject} no longer holds {ReviewStore.ReviewerRole}");
    return 0;
}

static int Blocked(string reason, string detail)
{
    Console.WriteLine($"BLOCKED {reason}: {detail}. Nothing was finalized; the review stays pending.");
    return 4;
}

static int Fail(int code, string message)
{
    Console.Error.WriteLine($"FAILED: {message} Nothing was finalized.");
    return code;
}

static int Usage()
{
    Console.Error.WriteLine("Usage: prepare [--claim id] [--requester id] [--amount n] | review <claim> <reviewer> <approve|reject>" +
                            " | resume <claim> | amend <claim> <amount> | revoke <subject> | --self-test" +
                            "   [--store dir] [--offline] [--now iso] [--graph next] [--rules next] [--skip-version-check]");
    return 2;
}

string? Option(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

string[] Positional()
{
    List<string> list = [];
    for (int i = 0; i < args.Length; i++)
    {
        if (valued.Contains(args[i])) i++;
        else if (!args[i].StartsWith("--", StringComparison.Ordinal)) list.Add(args[i]);
    }
    return [.. list];
}

IHost BuildHost()
{
    // Only --key=value switches are configuration (--LlmProvider:Provider=openai).
    HostApplicationBuilder builder = Host.CreateApplicationBuilder(
        args.Where(a => a.StartsWith("--", StringComparison.Ordinal) && a.Contains('=')).ToArray());
    builder.Services.AddLlmChatClient(builder.Configuration);
    return builder.Build();
}
