using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Ch08.ActionPolicy;
using Ch12.ReceiptExtraction;
using Contoso.AuthorizedReimbursement;
using Microsoft.Extensions.AI;

namespace Contoso.Receipts;

/// <summary>Where a receipt stands: waiting for a person, refused at intake, or a v2 claim state.</summary>
public sealed record IntakeStatus(Guid SubmissionId, string State, string Reason);

/// <summary>
/// The receipt front of the reimbursement desk:
///   pages -> vision reading -> validation -> ready? -> v2 ReimbursementDesk
///                                         -> blocked -> AwaitingVerification -> a reviewer types the value
/// The desk is the v2 snapshot's, unchanged. It asks its IChatClient for a
/// ProposalDraft; here that client is <see cref="VerifiedProposals"/>, which
/// answers with the validated (or person-verified) values, so no model writes
/// the amount the policy judges.
/// </summary>
public sealed class ReceiptIntake
{
    private sealed record Pending(string Tenant, string Requester, ReceiptProposal Receipt);

    private readonly ReceiptReader _reader;
    private readonly PermissionDirectory _directory;
    private readonly VerifiedProposals _proposals;
    private readonly Func<DateOnly> _today;
    private readonly ConcurrentDictionary<(string, Guid), Lazy<Task<IntakeStatus>>> _submissions = new();
    private readonly ConcurrentDictionary<(string, string), Guid> _receipts = new();     // (tenant, image hash) -> submission
    private readonly ConcurrentDictionary<(string, Guid), Pending> _pending = new();

    public ReceiptIntake(ReceiptReader reader, PermissionDirectory directory, VerifiedProposals proposals,
        ReimbursementDesk desk, Func<DateOnly> today)
    {
        _reader = reader;
        _directory = directory;
        _proposals = proposals;
        Desk = desk;
        _today = today;
    }

    public ReimbursementDesk Desk { get; }

    /// <summary>
    /// The employee submits receipt pages. A repeated submission id (a double
    /// click, even a concurrent one) shares the first submission's result; the
    /// same images under another id are refused: one receipt, one claim.
    /// </summary>
    public Task<IntakeStatus> SubmitAsync(Caller employee, Guid submissionId, IReadOnlyList<byte[]> pages, CancellationToken ct) =>
        _submissions.GetOrAdd((employee.TenantId, submissionId),
            _ => new Lazy<Task<IntakeStatus>>(() => IntakeAsync(employee, submissionId, pages, ct))).Value;

    private async Task<IntakeStatus> IntakeAsync(Caller employee, Guid id, IReadOnlyList<byte[]> pages, CancellationToken ct)
    {
        string hash = Convert.ToHexString(SHA256.HashData(pages.SelectMany(p => p).ToArray()));
        if (_receipts.GetOrAdd((employee.TenantId, hash), id) != id)
            return new(id, "Rejected", "duplicate_receipt");

        IReadOnlyList<ReceiptReading?> readings;
        try
        {
            readings = await _reader.ReadAsync(pages, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException ||
                                   (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            // Nothing was read: forget the submission so that a resubmit tries again.
            _receipts.TryRemove(new((employee.TenantId, hash), id));
            _submissions.TryRemove((employee.TenantId, id), out _);
            return new(id, "Rejected", "reading_failed");
        }
        ReceiptProposal receipt = ReceiptValidator.Validate(id.ToString("D"), readings, _today());
        if (!receipt.ReadyForPolicy)
        {
            _pending[(employee.TenantId, id)] = new(employee.TenantId, employee.SubjectId, receipt);
            return new(id, "AwaitingVerification", string.Join(", ", receipt.Blocking));
        }
        return await ForwardAsync(employee, id, receipt.Total.Value!.Value, receipt.Currency.Value!, "validated receipt", ct);
    }

    /// <summary>
    /// A finance reviewer, not the requester, reads the paper receipt and types
    /// the total and currency. Only then does the claim enter the desk, where
    /// the policy judges it like any other.
    /// </summary>
    public async Task<IntakeStatus> VerifyAsync(Caller reviewer, Guid submissionId, decimal total, string currency, CancellationToken ct)
    {
        if (!_pending.TryGetValue((reviewer.TenantId, submissionId), out Pending? pending))  // tenant from authentication
            return new(submissionId, "Refused", "unknown_receipt");
        if (!reviewer.Permissions.Contains(Roles.FinanceReviewer))
            return new(submissionId, "Refused", "reviewer_not_authorized");
        if (reviewer.SubjectId == pending.Requester)
            return new(submissionId, "Refused", "separation_of_duties");
        if (!_pending.TryRemove(new((reviewer.TenantId, submissionId), pending)))
            return new(submissionId, "Refused", "already_verified");

        Caller requester = _directory.Authenticate(pending.Tenant, pending.Requester);   // current rights, not the ones at submit
        return await ForwardAsync(requester, submissionId, total, currency, $"total verified by {reviewer.SubjectId}", ct);
    }

    public bool IsPending(string tenant, Guid submissionId) => _pending.ContainsKey((tenant, submissionId));

    private async Task<IntakeStatus> ForwardAsync(Caller requester, Guid id, decimal amount, string currency,
        string source, CancellationToken ct)
    {
        _proposals.Set(id, new ProposalDraft(requester.SubjectId, amount, currency, source));
        ClaimStatus status = await Desk.SubmitAsync(requester,
            new ExpenseClaim(id, requester.SubjectId, VerifiedProposals.ClaimText(id)), ct);
        return new(id, status.State.ToString(), status.Reason);
    }
}

/// <summary>
/// The v2 desk's proposal source for receipts: answers the desk's structured
/// ProposalDraft request with the values validation (or a reviewer) produced
/// for that submission. Not a model: it never writes a value of its own, and
/// an unknown submission gets no proposal.
/// </summary>
public sealed class VerifiedProposals : IChatClient
{
    private readonly ConcurrentDictionary<Guid, ProposalDraft> _drafts = new();

    public static string ClaimText(Guid id) => $"receipt:{id:D}";

    public void Set(Guid id, ProposalDraft draft) => _drafts[id] = draft;

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        string text = messages.Last(m => m.Role == ChatRole.User).Text;
        string json = _drafts.FirstOrDefault(d => text.Contains(ClaimText(d.Key))).Value is { } draft
            ? JsonSerializer.Serialize(draft, ReceiptReader.Json)
            : "no proposal";
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
        yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

internal static class Money
{
    public static string Show(decimal? amount) => amount?.ToString("0.00", CultureInfo.InvariantCulture) ?? "-";
}
