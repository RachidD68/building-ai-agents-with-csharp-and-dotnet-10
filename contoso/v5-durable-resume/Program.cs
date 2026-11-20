// ============================================================================
// Contoso v5: durable resume
// ----------------------------------------------------------------------------
// v4's receipt claim, with the wait for the reviewer made durable:
//
//   prepare   Alice's taxi receipt goes through v4's intake into the v2 desk
//             (AwaitingReview, approval_missing); the review workflow runs to
//             its port; checkpoint, session and graph version go to disk; exit
//   review    a reviewer's decision, checked and bound to the amount of record
//             and the account version, is written to app.db; nothing waits
//   resume    a new process: lease (fencing token), v2's own repair and
//             reconciliation, graph version check, the decision revalidated
//             against the records as they are now, then the checkpoint is
//             restored and the pay step hands the decision to the v2 desk,
//             which commits once and pays once
//
//   dotnet run -- prepare [--live]           --live reads the receipt with the
//                                             configured vision model (VisionModel)
//   dotnet run -- review <op|latest> <reviewer> <approve|reject>
//   dotnet run -- resume <op|latest> [--pause-at <point>]
//   dotnet run -- status <op|latest>
//   dotnet run -- revoke <subject>            the identity system drops the reviewer role
//   dotnet run -- account-changed <employee>  another claim committed first (version + 1)
//   dotnet run -- --self-test                 every verb as a child process, offline
// Options: --store <dir>, --now <ISO time>, --graph next, --lease-seconds <n>.
// Points (for Ch15.RestartTests): before-commit, before-effect, after-accept, after-record.
// Exit codes: 0 done, 2 usage, 3 failed, 4 blocked, 5 waiting.
// Simulated effects only: the provider is v2's fake with its own journal.
// ============================================================================

using System.Globalization;
using Ch08.ActionPolicy;
using Ch08.IdempotentCommit;
using Ch08.UncertainOutcome;
using Ch12.ReceiptExtraction;
using Ch15.DurableReview;
using Ch15.ExternalEffects;
using Contoso.AuthorizedReimbursement;
using Contoso.DurableResume;
using Contoso.Receipts;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

if (args.Contains("--self-test")) return V5SelfTest.Run();

string[] valued = ["--store", "--now", "--graph", "--lease-seconds", "--pause-at"];
string[] words = Positional();
string storePath = Option("--store") ?? Path.Combine(Path.GetTempPath(), "AgenticAI", "Contoso.DurableResume");
Directory.CreateDirectory(storePath);
string appDb = Path.Combine(storePath, "app.db");
string journal = Path.Combine(storePath, "provider.db");
string checkpointsPath = Path.Combine(storePath, "checkpoints");
DateTimeOffset now = Option("--now") is { } at
    ? DateTimeOffset.Parse(at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : DateTimeOffset.UtcNow;
bool nextGraph = Option("--graph") == "next";
// The lease is checked against the live clock (or the --now override, fixed).
Func<DateTimeOffset> clock = Option("--now") is null ? () => DateTimeOffset.UtcNow : () => now;
IStepHook hook = Option("--pause-at") is { } point ? new StdinPause(point) : new NoHook();

DurableClaims durable = new(appDb);
// The identity system's seed, once per store (prepare); a later revoke must stay revoked.
if (words.FirstOrDefault() == "prepare") durable.SeedIdentities();
PermissionDirectory directory = durable.LoadDirectory();
ReimbursementDesk desk = new(new VerifiedProposals(), appDb, new HookedProvider(new FakePaymentProvider(journal), hook),
    directory, () => now);
if (desk.Store.LoadAccount(DurableClaims.Tenant, "E-1001") is null)
{
    desk.Store.SeedAccount(new EmployeeAccount(DurableClaims.Tenant, "E-1001", "EUR", 500m, 7));
    desk.Store.SeedAccount(new EmployeeAccount(DurableClaims.Tenant, "E-2002", "EUR", 300m, 3));
}

try
{
    return words.FirstOrDefault() switch
    {
        "prepare" => await PrepareAsync(),
        "review" when words.Length >= 4 => Review(words[1], words[2], words[3]),
        "resume" when words.Length >= 2 => await ResumeAsync(words[1]),
        "status" when words.Length >= 2 => Status(words[1]),
        "revoke" when words.Length >= 2 => Revoke(words[1]),
        "account-changed" when words.Length >= 2 => AccountChanged(words[1]),
        _ => Usage(),
    };
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
}

async Task<int> PrepareAsync()
{
    Console.WriteLine("=== Contoso v5: prepare ===\n");
    IChatClient vision;
    IHost? host = null;
    if (args.Contains("--live"))
    {
        host = Host.CreateApplicationBuilder([]).Build();
        IConfiguration config = host.Services.GetRequiredService<IConfiguration>();
        if (VisionClient.Build(config) is not { } configured)
        {
            Console.WriteLine("[skipped: configure a vision model under \"VisionModel\" (Ollama llava, or OpenAI)]");
            return 0;
        }
        vision = configured;
        Console.WriteLine($"Vision model: {VisionClient.Describe(config)}");
    }
    else
    {
        vision = new ScriptedReceiptReading();
        Console.WriteLine("Receipt reading: scripted (the clean taxi receipt as an honest reader reports it)");
    }

    using (host)
    {
        VerifiedProposals proposals = new();
        ReimbursementDesk intakeDesk = new(proposals, appDb, new FakePaymentProvider(journal), directory, () => now);
        ReceiptIntake intake = new(new ReceiptReader(vision), directory, proposals, intakeDesk, () => DateOnly.FromDateTime(now.UtcDateTime));
        Caller alice = directory.Authenticate(DurableClaims.Tenant, "E-1001");
        Guid op = Guid.NewGuid();
        byte[] page = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "assets", "receipts", "taxi-clean.png"));
        IntakeStatus intakeStatus = await intake.SubmitAsync(alice, op, [page], CancellationToken.None);
        Console.WriteLine($"Intake: {intakeStatus.State} ({intakeStatus.Reason})");
        if (intakeStatus.State != "AwaitingReview")
        {
            Console.WriteLine("Nothing waits for a reviewer, so there is no review to make durable.");
            return intakeStatus.State == "Rejected" ? 4 : 0;
        }

        decimal amount = intakeDesk.AmountOfRecord(DurableClaims.Tenant, op)!.Value;
        long version = intakeDesk.Store.LoadAccount(DurableClaims.Tenant, "E-1001")!.Version;
        Workflow workflow = ContosoReviewGraph.Build(pay: null, nextGraph);
        string graph = GraphVersion.Of(workflow, ContosoReviewGraph.RulesVersion);

        Directory.CreateDirectory(checkpointsPath);
        using FileSystemJsonCheckpointStore files = new(new DirectoryInfo(checkpointsPath));
        CheckpointManager checkpoints = CheckpointManager.CreateJson(files);
        CheckpointInfo? paused = null;
        bool requested = false;
        await using (StreamingRun run = await InProcessExecution.RunStreamingAsync(workflow,
            new ClaimTicket(op, "E-1001", amount, "EUR", version), checkpoints, Guid.NewGuid().ToString("N")))
        {
            await foreach (WorkflowEvent evt in run.WatchStreamAsync())
            {
                if (evt is RequestInfoEvent) requested = true;
                if (evt is SuperStepCompletedEvent { CompletionInfo.Checkpoint: { } cp })
                {
                    paused = cp;
                    if (requested) break;
                }
            }
        }
        if (paused is null || !requested)
        {
            Console.Error.WriteLine("FAILED: the review workflow never reached its port.");
            return 3;
        }
        durable.SaveSession(new SessionRow(op, "E-1001", paused.SessionId, paused.CheckpointId, graph));
        Console.WriteLine($"Claim:      {op} ({Money.Format(amount)} EUR, account version {version})");
        Console.WriteLine($"Checkpoint: {paused.CheckpointId}");
        Console.WriteLine($"Graph:      {graph}");
        Console.WriteLine("\nThis process now exits; the claim waits on disk for a reviewer.");
        return 0;
    }
}

int Review(string id, string reviewerId, string word)
{
    Console.WriteLine("=== Contoso v5: review ===\n");
    if (durable.LoadSession(id) is not { } session) return Fail(3, $"no paused review for {id}.");
    if (word is not ("approve" or "reject")) return Fail(2, $"unknown decision \"{word}\" (use approve or reject).");
    ClaimStatus status = desk.Status(DurableClaims.Tenant, session.OperationId);
    Caller reviewer = directory.Authenticate(DurableClaims.Tenant, reviewerId);
    string? refused = status.State != ClaimState.AwaitingReview ? "not_awaiting_review"
        : !reviewer.Permissions.Contains(Roles.FinanceReviewer) ? "reviewer_not_authorized"
        : reviewerId == session.Requester ? "separation_of_duties"
        : null;
    if (refused is not null)
    {
        Console.WriteLine($"REFUSED {refused}: nothing recorded.");
        return 4;
    }
    decimal amount = desk.AmountOfRecord(DurableClaims.Tenant, session.OperationId)!.Value;
    long version = desk.Store.LoadAccount(DurableClaims.Tenant, session.Requester)!.Version;
    if (!durable.RecordDecision(session.OperationId, reviewerId, word == "approve", amount, version, now))
    {
        Console.WriteLine("REFUSED already_decided: nothing recorded.");
        return 4;
    }
    Console.WriteLine($"Decision recorded: {word} by {reviewerId} for {Money.Format(amount)} EUR at account version {version}");
    return 0;
}

async Task<int> ResumeAsync(string id)
{
    Console.WriteLine("=== Contoso v5: resume ===\n");
    if (durable.LoadSession(id) is not { } session) return Fail(3, $"no paused review for {id}.");
    Guid op = session.OperationId;

    TimeSpan leaseFor = TimeSpan.FromSeconds(double.Parse(Option("--lease-seconds") ?? "30", CultureInfo.InvariantCulture));
    Lease? lease = durable.Leases.TryAcquire(DurableClaims.LeaseKey(op), $"worker-{Environment.ProcessId}", clock(), leaseFor);
    if (lease is null)
    {
        Console.WriteLine($"LEASE HELD by {durable.Leases.Load(DurableClaims.LeaseKey(op))?.Owner}; this worker does nothing.");
        return 5;
    }
    using IDisposable keepAlive = durable.Leases.KeepAlive(DurableClaims.LeaseKey(op), lease.Token, leaseFor, clock);
    try
    {
        // v2's own restart work first: claims stranded between steps, outbox rows, unknown payments.
        IReadOnlyList<ReconcileResult> reconciled = await desk.ResumeAsync(CancellationToken.None);
        foreach (ReconcileResult r in reconciled)
            Console.WriteLine($"Reconciled {r.OperationId}: {r.Status} ({r.How})");

        Workflow workflow = ContosoReviewGraph.Build(
            new PayContext(desk, directory, durable.Leases, lease.Token, hook, clock), nextGraph);
        string running = GraphVersion.Of(workflow, ContosoReviewGraph.RulesVersion);
        if (session.GraphVersion != running)
            return Blocked("graph_version_mismatch", $"paused by {session.GraphVersion}, this build runs {running}");

        ReviewVerdict? verdict;
        ClaimStatus status = desk.Status(DurableClaims.Tenant, op);
        if (status.State == ClaimState.AwaitingReview)
        {
            // The decision is used now, so it is checked now, against the records as they are.
            DecisionRow? d = durable.LatestDecision(op);
            if (d is null)
            {
                Console.WriteLine($"WAITING: no decision recorded for {op}. Nothing was resumed.");
                return 5;
            }
            decimal amount = desk.AmountOfRecord(DurableClaims.Tenant, op)!.Value;
            long version = desk.Store.LoadAccount(DurableClaims.Tenant, session.Requester)!.Version;
            string? blocked = d.Amount != amount || d.AccountVersion != version ? "stale_approval"
                : now > d.ExpiresAt ? "approval_expired"
                : !directory.Authenticate(DurableClaims.Tenant, d.ReviewerId).Permissions.Contains(Roles.FinanceReviewer)
                    ? "reviewer_revoked"
                : null;
            if (blocked is not null)
                return Blocked(blocked, blocked switch
                {
                    "stale_approval" => $"the decision covers {Money.Format(d.Amount)} EUR at account version {d.AccountVersion}; " +
                                        $"the claim is now {Money.Format(amount)} EUR at version {version}",
                    "reviewer_revoked" => $"{d.ReviewerId} no longer holds {Roles.FinanceReviewer}",
                    _ => $"the decision expired at {d.ExpiresAt:yyyy-MM-dd HH:mm}Z",
                });
            verdict = new ReviewVerdict(op, d.ReviewerId, d.Approve, d.Amount, d.AccountVersion);
            Console.WriteLine($"Decision: {(d.Approve ? "approve" : "reject")} by {d.ReviewerId}, still valid");
        }
        else
        {
            // Committed before a crash: the decision was used already. The pay step re-reads the claim.
            DecisionRow d = durable.LatestDecision(op)!;
            verdict = new ReviewVerdict(op, d.ReviewerId, d.Approve, d.Amount, d.AccountVersion);
            Console.WriteLine($"Claim already {status.State}: resuming to report it, not to decide again");
        }

        using FileSystemJsonCheckpointStore files = new(new DirectoryInfo(checkpointsPath));
        CheckpointManager checkpoints = CheckpointManager.CreateJson(files);
        await using StreamingRun run = await InProcessExecution.ResumeStreamingAsync(workflow,
            new CheckpointInfo(session.SessionId, session.CheckpointId), checkpoints);
        Console.WriteLine($"Restored checkpoint {session.CheckpointId} (the paused review) in process {Environment.ProcessId}");
        await foreach (WorkflowEvent evt in run.WatchStreamAsync())
        {
            if (evt is RequestInfoEvent request) await run.SendResponseAsync(request.Request.CreateResponse(verdict));
            if (evt is WorkflowOutputEvent { Data: ClaimResult result })
            {
                Console.WriteLine($"[{result.State.ToUpperInvariant()}] {result.OperationId} ({result.Reason}) " +
                                  $"{result.PaymentId}: {result.Detail}");
                return result.State is "Delivered" or "Reconciled" or "Rejected" ? 0 : result.State == "StaleLease" ? 4 : 3;
            }
            if (evt is WorkflowErrorEvent error) return Fail(3, $"the resumed review failed: {error.Exception?.Message}");
        }
        return Fail(3, "the resumed review produced no outcome.");
    }
    catch (InvalidDataException ex)
    {
        return Blocked("checkpoint_incompatible", ex.Message);
    }
    finally
    {
        durable.Leases.Release(DurableClaims.LeaseKey(op), lease.Token, clock());
    }
}

int Status(string id)
{
    if (durable.LoadSession(id) is not { } session) return Fail(3, $"no paused review for {id}.");
    ClaimStatus status = desk.Status(DurableClaims.Tenant, session.OperationId);
    IReadOnlyList<string> refs = DurableClaims.JournalReferences(journal);
    Console.WriteLine($"claim:      {session.OperationId} {status.State} ({status.Reason}) {status.Detail}");
    Console.WriteLine($"delivery:   {desk.Worker.Load(DurableClaims.Tenant, session.OperationId)?.Status.ToString() ?? "none"}");
    Console.WriteLine($"operations: {desk.Store.Count("operations")}");
    Console.WriteLine($"effects:    {refs.Count}");
    Console.WriteLine($"references: {string.Join(" ", refs)}");
    return 0;
}

int Revoke(string subject)
{
    durable.Revoke(subject, Roles.FinanceReviewer);
    Console.WriteLine($"Revoked: {subject} no longer holds {Roles.FinanceReviewer}");
    return 0;
}

int AccountChanged(string employee)
{
    EmployeeAccount account = desk.Store.LoadAccount(DurableClaims.Tenant, employee)!;
    desk.Store.SeedAccount(account with { Version = account.Version + 1 });
    Console.WriteLine($"Account {employee} moved to version {account.Version + 1} (another claim committed first)");
    return 0;
}

static int Blocked(string reason, string detail)
{
    Console.WriteLine($"BLOCKED {reason}: {detail}. Nothing was committed; the claim still waits for review.");
    return 4;
}

static int Fail(int code, string message)
{
    Console.Error.WriteLine($"FAILED: {message}");
    return code;
}

static int Usage()
{
    Console.Error.WriteLine("Usage: prepare [--live] | review <op|latest> <reviewer> <approve|reject> | resume <op|latest>" +
                            " | status <op|latest> | revoke <subject> | account-changed <employee> | --self-test" +
                            "   [--store dir] [--now iso] [--graph next] [--lease-seconds n] [--pause-at point]");
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

internal static class Money
{
    public static string Format(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);
}
