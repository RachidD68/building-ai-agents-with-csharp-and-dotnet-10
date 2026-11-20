using Ch08.UncertainOutcome;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.InProc;

namespace Ch15.ExternalEffects;

/// <summary>
/// One worker process's side of a payout. Three durable states, three owners,
/// three files under the store folder:
///   checkpoints.db  the framework's checkpoints (SqliteCheckpointStore)
///   app.db          the business record: payouts, leases, Ch08's deliveries
///   provider.db     the simulated provider's own journal (Ch08's FakePaymentProvider)
/// </summary>
public sealed class PayoutHost(string storeFolder, Func<DateTimeOffset>? clock = null)
{
    public const string Tenant = "contoso";
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);

    public PayoutStore Store { get; } = new(Path.Combine(Directory.CreateDirectory(storeFolder).FullName, "app.db"));
    public string CheckpointsPath => Path.Combine(storeFolder, "checkpoints.db");
    public string JournalPath => Path.Combine(storeFolder, "provider.db");

    /// <summary>
    /// Takes the lease, then restores the workflow from the restart point (or
    /// starts it from the business record the first time) and runs it to its
    /// outcome. Returns null when another worker holds the lease or the run failed.
    /// </summary>
    public async Task<PayoutResult?> RunAsync(string claimId, string owner, TimeSpan leaseFor, IStepHook hook,
        ProviderFault fault = ProviderFault.None, bool naive = false, bool renew = true, CancellationToken ct = default)
    {
        PayoutRow? row = Store.Load(claimId);
        if (row is null)
        {
            Console.WriteLine($"FAILED: no approved payout for {claimId}.");
            return null;
        }
        Lease? lease = Store.TryAcquireLease(claimId, owner, _clock(), leaseFor);
        if (lease is null)
        {
            Lease? held = Store.LoadLease(claimId);
            Console.WriteLine($"LEASE HELD by {held?.Owner} (token {held?.Token}) until {held?.ExpiresAt:HH:mm:ss}Z; this worker does nothing.");
            return null;
        }
        Console.WriteLine($"Lease: {owner} holds token {lease.Token} until {lease.ExpiresAt:HH:mm:ss}Z{(renew ? ", renewed while this process lives" : ", not renewed")}");
        using IDisposable? keepAlive = renew ? Store.Leases.KeepAlive(claimId, lease.Token, leaseFor, _clock) : null;

        IPaymentProvider provider = new HookedProvider(new FakePaymentProvider(JournalPath, fault), hook);
        PayoutContext context = new(Store, new DeliveryWorker(Store.DatabasePath, provider), lease.Token, hook, _clock, naive);
        Workflow workflow = PayoutGraph.Build(context);
        // Lockstep: the next superstep starts only after this loop has handled the events of the last one,
        // so the restart point is on disk before the payout step can begin.
        InProcessExecutionEnvironment env = InProcessExecution.Lockstep.WithCheckpointing(
            CheckpointManager.CreateJson(new SqliteCheckpointStore(CheckpointsPath)));

        StreamingRun run;
        bool fresh = row.CheckpointId is null;
        if (fresh)
        {
            PayoutOrder order = new(row.ClaimId, row.Tenant, row.OperationId, row.EmployeeId, row.Amount, row.Currency);
            run = await env.RunStreamingAsync(workflow, order, Guid.NewGuid().ToString("N"), ct);
            Console.WriteLine($"Started payout of {row.Amount} {row.Currency} for {claimId}, operation {row.OperationId}");
        }
        else
        {
            run = await env.ResumeStreamingAsync(workflow, new CheckpointInfo(row.SessionId!, row.CheckpointId!), ct);
            Console.WriteLine($"Restored checkpoint {row.CheckpointId} (taken before the payout step); operation {row.OperationId}");
        }

        await using (run)
        try
        {
            await foreach (WorkflowEvent evt in run.WatchStreamAsync(ct))
            {
                // The first checkpoint of a fresh run follows authorize: it holds the order for the payout step.
                if (fresh && evt is SuperStepCompletedEvent { CompletionInfo.Checkpoint: { } cp })
                {
                    Store.SaveRestartPoint(claimId, cp.SessionId, cp.CheckpointId);
                    fresh = false;
                }
                if (evt is WorkflowOutputEvent { Data: PayoutResult result })
                {
                    Console.WriteLine($"Outcome: {result.Status}: {result.Detail}");
                    return result;
                }
                if (evt is WorkflowErrorEvent error)
                {
                    Console.WriteLine($"FAILED: {error.Exception?.Message}");
                    return null;
                }
            }
        }
        finally
        {
            Store.ReleaseLease(claimId, lease.Token, _clock());
        }
        Console.WriteLine("FAILED: the workflow ended without an outcome.");
        return null;
    }

    /// <summary>Exit code for an outcome: 0 Paid or Cancelled, 6 Unknown, 4 refused, 5 nothing done.</summary>
    public static int ExitCode(PayoutResult? r) => r?.Status switch
    {
        "Paid" or "Cancelled" => 0,
        "Unknown" => 6,
        null => 5,
        _ => 4,
    };

    /// <summary>What the three owners say now. Printed by the status verb; read by the restart tests.</summary>
    public void PrintStatus(string claimId)
    {
        PayoutRow? row = Store.Load(claimId);
        Delivery? delivery = row is null ? null
            : new DeliveryWorker(Store.DatabasePath, new FakePaymentProvider(JournalPath)).Load(row.Tenant, row.OperationId);
        IReadOnlyList<(string Reference, string PaymentId)> journal = PayoutStore.ReadJournal(JournalPath);
        Console.WriteLine($"payout:     {row?.State ?? "none"} (operation {row?.OperationId}, fence {row?.Fence}, cancel requested {row?.CancelRequested})");
        Console.WriteLine($"delivery:   {delivery?.Status.ToString() ?? "none"} {delivery?.PaymentId}");
        Lease? lease = Store.LoadLease(claimId);
        Console.WriteLine($"lease:      {(lease is null ? "none" : $"{lease.Owner} token {lease.Token}")}");
        Console.WriteLine($"effects:    {journal.Count}");
        Console.WriteLine($"references: {string.Join(" ", journal.Select(j => j.Reference))}");
        Console.WriteLine($"checkpoints: {new SqliteCheckpointStore(CheckpointsPath).Count()}");
    }
}
