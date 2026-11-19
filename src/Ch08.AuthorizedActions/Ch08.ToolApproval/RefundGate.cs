using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ch08.ActionPolicy;
using Microsoft.Extensions.AI;

namespace Ch08.ToolApproval;

/// <summary>An order as the order system stores it.</summary>
internal sealed record Order(string OrderId, string CustomerId, decimal RefundableAmount, string Currency, long Version);

/// <summary>What the reviewer decided, with the identity the server authenticated.</summary>
internal sealed record ReviewerDecision(string ReviewerId, string ReviewerRole, bool Approved);

/// <summary>
/// The application side of a refund_order call. Before anyone is asked to
/// approve, the call's arguments are turned into a proposal and checked by
/// ActionPolicy. ActionPolicy is written for Contoso's expense reimbursements;
/// an order refund has the same shape (an amount and a currency against a
/// versioned balance in one tenant), so the order's refundable balance stands
/// in for the account. The approval store is server-side: only
/// <see cref="RecordReview"/> writes to it, after the reviewer is known.
/// </summary>
internal sealed class RefundGate(string tenant, Caller admin, RefundLedger ledger, IEnumerable<Order> orders)
{
    private readonly Dictionary<string, Order> _orders =
        orders.ToDictionary(o => o.OrderId, StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, ApprovalRecord> _approvals = new();

    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;

    public RefundLedger Ledger => ledger;

    /// <summary>ActionPolicy over the call's arguments and the stored approval, if any.</summary>
    public PolicyResult Check(FunctionCallContent call)
    {
        if (!TryReadProposal(call, out EmployeeAccount? account, out ReimbursementProposal? proposal, out string problem))
            return new(Decision.Deny, problem);
        _approvals.TryGetValue(proposal.OperationId, out ApprovalRecord? approval);
        return ReimbursementPolicy.Evaluate(admin, account, proposal, approval, Clock());
    }

    /// <summary>
    /// Asks the reviewer, stores the decision bound to these exact arguments,
    /// and returns true only if the policy now allows the call (an approval by
    /// the requester, or by someone without the reviewer role, does not count).
    /// </summary>
    public bool RecordReview(FunctionCallContent call, Func<FunctionCallContent, ReviewerDecision> reviewer)
    {
        if (!TryReadProposal(call, out _, out ReimbursementProposal? proposal, out _)) return false;
        ReviewerDecision decision = reviewer(call);
        _approvals[proposal.OperationId] = new ApprovalRecord(
            ReimbursementPolicy.Digest(admin.TenantId, admin.SubjectId, proposal),
            admin.TenantId, admin.SubjectId, decision.ReviewerId, decision.ReviewerRole,
            ReimbursementPolicy.Version, Clock().AddMinutes(15), decision.Approved);
        return Check(call).Decision == Decision.Allow;
    }

    /// <summary>
    /// The tool body. It runs only after the framework approved the call, and
    /// it checks the policy again (the state may have changed since the
    /// review) before the per-order ledger issues at most one credit.
    /// </summary>
    public string Refund(string orderId, decimal amount, string currency)
    {
        var call = new FunctionCallContent("tool-body", "refund_order", new Dictionary<string, object?>
        {
            ["orderId"] = orderId, ["amount"] = amount, ["currency"] = currency,
        });
        PolicyResult policy = Check(call);
        return policy.Decision == Decision.Allow
            ? ledger.Refund(orderId, amount, currency)
            : $"Refund refused ({policy.Reason}). No credit was issued.";
    }

    /// <summary>The business operation id: one per order refund, stable across retries.</summary>
    public Guid OperationIdFor(string orderId) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"{tenant}/refund/{orderId.Trim().ToUpperInvariant()}")).AsSpan(0, 16));

    private bool TryReadProposal(FunctionCallContent call,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out EmployeeAccount? account,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ReimbursementProposal? proposal,
        out string problem)
    {
        account = null; proposal = null; problem = "invalid_arguments";
        IDictionary<string, object?> args = call.Arguments ?? new Dictionary<string, object?>();
        if (call.Name != "refund_order" ||
            !args.TryGetValue("orderId", out object? rawOrder) || Text(rawOrder) is not { Length: > 0 } orderId ||
            !args.TryGetValue("amount", out object? rawAmount) || !RefundRule.TryReadAmount(rawAmount, out decimal amount) ||
            !args.TryGetValue("currency", out object? rawCurrency) || Text(rawCurrency) is not { } currency)
            return false;
        if (!_orders.TryGetValue(orderId.Trim(), out Order? order))
        {
            problem = "resource_scope";
            return false;
        }
        account = new EmployeeAccount(tenant, order.CustomerId, order.Currency, order.RefundableAmount, order.Version);
        proposal = new ReimbursementProposal(OperationIdFor(order.OrderId), order.CustomerId, amount, currency, order.Version);
        return true;
    }

    private static string? Text(object? raw) => raw switch
    {
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
        _ => null,
    };
}
