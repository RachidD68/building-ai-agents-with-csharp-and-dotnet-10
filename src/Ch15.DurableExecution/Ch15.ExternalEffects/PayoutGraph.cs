// ============================================================================
// The payout graph:  authorize --> payout --> settle
//
// authorize ends superstep 1, so the checkpoint taken after it holds the
// PayoutOrder message for the payout step. That checkpoint is the restart
// point: a worker killed anywhere inside the payout step resumes from it, and
// the payout step runs again. Running again must be safe, so the step
//   - re-reads the business record first (Paid stays Paid, no provider call),
//   - uses the operation id from the order, never a new one,
//   - reconciles an attempt the delivery worker left unresolved,
//   - commits only under the current lease (fencing token).
// ============================================================================

using Ch08.UncertainOutcome;
using Microsoft.Agents.AI.Workflows;

namespace Ch15.ExternalEffects;

/// <summary>What the payout step needs to do its work: one per claim, issued at approval.</summary>
public sealed record PayoutOrder(string ClaimId, string Tenant, Guid OperationId, string EmployeeId, string Amount, string Currency);

/// <summary>Paid, Unknown (reconciliation pending), Cancelled, Conflict or StaleLease.</summary>
public sealed record PayoutResult(string ClaimId, Guid OperationId, string Status, string Detail, string? PaymentId);

/// <summary>This process's view of the payout: stores, delivery worker, lease token, hooks.</summary>
public sealed record PayoutContext(PayoutStore Store, DeliveryWorker Worker, long Token, IStepHook Hook,
    Func<DateTimeOffset> Clock, bool Naive = false);

public static class PayoutGraph
{
    public static Workflow Build(PayoutContext context)
    {
        ExecutorBinding authorize = new AuthorizeExecutor(context.Store).BindExecutor();
        ExecutorBinding payout = new PayoutExecutor(context).BindExecutor();
        ExecutorBinding settle = new SettleExecutor().BindExecutor();
        return new WorkflowBuilder(authorize)
            .AddEdge(authorize, payout)
            .AddEdge(payout, settle)
            .WithOutputFrom(settle)
            .WithName("ContosoPayout")
            .Build();
    }
}

/// <summary>The order must be the business record's: same claim, same operation id, same amount.</summary>
internal sealed class AuthorizeExecutor(PayoutStore store) : Executor<PayoutOrder, PayoutOrder>("authorize")
{
    public override ValueTask<PayoutOrder> HandleAsync(PayoutOrder order, IWorkflowContext context, CancellationToken ct)
    {
        PayoutRow? row = store.Load(order.ClaimId);
        if (row is null || row.OperationId != order.OperationId || row.Amount != order.Amount)
            throw new InvalidOperationException($"order for {order.ClaimId} does not match the business record");
        return ValueTask.FromResult(order);
    }
}

internal sealed class PayoutExecutor(PayoutContext ctx) : Executor<PayoutOrder, PayoutResult>("payout")
{
    private const string TooLate =
        "; the cancel came too late to stop the payout: cancelling is not undoing, a refund is a new approved operation";

    public override async ValueTask<PayoutResult> HandleAsync(PayoutOrder order, IWorkflowContext context, CancellationToken ct)
    {
        if (ctx.Naive) return await NaiveAsync(order, ct);

        // 1. The business record first. A replay of a step that already paid stops here.
        PayoutRow row = ctx.Store.Load(order.ClaimId)!;
        if (row.State is "Paid" or "Cancelled" or "Conflict")
            return Result(order, row.State, $"re-read the operation record: already {row.State}" +
                (row.PaymentId is { } paid ? $" as {paid}" : "") + "; no provider call", row.PaymentId, row.CancelRequested);

        await ctx.Hook.ReachAsync(StepPoints.BeforeCommit);

        // 2. A cancel still stops a payout that has not begun (Approved). Once begun, the attempt is settled first.
        if (ctx.Store.Load(order.ClaimId)!.CancelRequested && row.State == "Approved")
            return ctx.Store.Cancel(order.ClaimId, ctx.Token, ctx.Clock())
                ? Result(order, "Cancelled", "cancelled before the commit; nothing was sent", null)
                : Stale(order);

        // 3. Fenced: only the current lease holder may begin (or take over) the payout.
        if (!ctx.Store.BeginPayout(order.ClaimId, ctx.Token, ctx.Clock())) return Stale(order);

        // 4. The effect, under the order's operation id. An attempt that a dead
        //    worker left Sending or OutcomeUnknown is reconciled, never resent blind.
        string how;
        if (ctx.Worker.Load(order.Tenant, order.OperationId) is { Status: DeliveryStatus.Sending or DeliveryStatus.OutcomeUnknown })
        {
            IReadOnlyList<ReconcileResult> settled = await ctx.Worker.ResumeAsync(ct);
            how = "reconciled from the restored workflow: " +
                  (settled.FirstOrDefault(r => r.OperationId == order.OperationId)?.How ?? "no result");
        }
        else
        {
            DeliveryStatus sent = await ctx.Worker.DeliverAsync(
                new Delivery(order.Tenant, order.OperationId, order.EmployeeId, order.Amount, order.Currency), ct);
            how = sent switch
            {
                DeliveryStatus.Delivered => "paid",
                DeliveryStatus.OutcomeUnknown => "outcome unknown: the provider may have paid; entered reconciliation",
                _ => sent.ToString(),
            };
        }

        Delivery delivery = ctx.Worker.Load(order.Tenant, order.OperationId)!;
        string state = delivery.Status switch
        {
            DeliveryStatus.Delivered or DeliveryStatus.Reconciled => "Paid",
            DeliveryStatus.Conflict => "Conflict",
            _ => "Unknown",
        };

        // 5. Fenced again: a worker whose lease was taken over during the call records nothing.
        if (!ctx.Store.RecordPayout(order.ClaimId, ctx.Token, state, delivery.PaymentId, ctx.Clock()))
            return Stale(order);
        await ctx.Hook.ReachAsync(StepPoints.AfterRecord);
        return Result(order, state, how + (delivery.PaymentId is { } id && !how.Contains(id) ? $" ({id})" : ""), delivery.PaymentId,
            ctx.Store.Load(order.ClaimId)!.CancelRequested);
    }

    /// <summary>
    /// The step as it is often first written: a fresh operation id per attempt,
    /// no look at the record. Replayed after a crash, it pays again.
    /// </summary>
    private async Task<PayoutResult> NaiveAsync(PayoutOrder order, CancellationToken ct)
    {
        Guid attempt = Guid.NewGuid();
        DeliveryStatus sent = await ctx.Worker.DeliverAsync(
            new Delivery(order.Tenant, attempt, order.EmployeeId, order.Amount, order.Currency), ct);
        string? paymentId = ctx.Worker.Load(order.Tenant, attempt)?.PaymentId;
        string state = sent == DeliveryStatus.Delivered ? "Paid" : "Unknown";
        ctx.Store.RecordNaive(order.ClaimId, attempt, state, paymentId);
        return new PayoutResult(order.ClaimId, attempt, state, $"naive attempt with a new operation id ({paymentId})", paymentId);
    }

    private static PayoutResult Result(PayoutOrder o, string status, string detail, string? paymentId, bool cancelled = false) =>
        new(o.ClaimId, o.OperationId, status, detail + (cancelled && status == "Paid" ? TooLate : ""), paymentId);

    private PayoutResult Stale(PayoutOrder o) =>
        new(o.ClaimId, o.OperationId, "StaleLease",
            $"lease token {ctx.Token} is no longer current (now {ctx.Store.LoadLease(o.ClaimId)?.Token}); nothing committed", null);
}

[YieldsOutput(typeof(PayoutResult))]
internal sealed class SettleExecutor() : Executor<PayoutResult>("settle")
{
    public override ValueTask HandleAsync(PayoutResult result, IWorkflowContext context, CancellationToken ct)
        => context.YieldOutputAsync(result, ct);
}
