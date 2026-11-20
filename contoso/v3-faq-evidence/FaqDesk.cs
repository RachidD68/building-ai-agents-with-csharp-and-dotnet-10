using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Ch08.ActionPolicy;
using Ch10.FaqAgent;
using Contoso.AuthorizedReimbursement;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Contoso.FaqEvidence;

/// <summary>How a question ended. Only Answered shows the model's text.</summary>
public enum FaqOutcome
{
    /// <summary>The answer cites only handbook sources retrieved for this question.</summary>
    Answered,
    /// <summary>Nothing scored at or above the threshold: abstained, model not called.</summary>
    NoEvidence,
    /// <summary>The model itself gave the abstention text.</summary>
    Declined,
    /// <summary>The model answered without citing a source: withheld.</summary>
    Uncited,
    /// <summary>The model cited a source that was not retrieved for this question: withheld.</summary>
    UnsupportedCitation,
    /// <summary>The caller is not an employee of the handbook's tenant: nothing searched.</summary>
    NotPermitted,
}

/// <summary>One retrieved chunk as recorded with the answer.</summary>
public sealed record EvidenceItem(string ChunkId, string Source, double Score);

/// <summary>What the employee sees, and the evidence record kept with it.</summary>
public sealed record FaqReply(
    Guid QuestionId, string Tenant, string Subject, string Question,
    FaqOutcome Outcome, string Text, IReadOnlyList<EvidenceItem> Evidence, IReadOnlyList<string> Cited);

/// <summary>
/// The Contoso FAQ desk: an employee's handbook question, answered from
/// retrieved evidence or not at all.
///   authenticated caller -> tenant check -> retrieve (threshold) -> no evidence: abstain
///   -> model with the excerpts -> citations checked against what was retrieved
///   -> Answered, or withheld (Uncited, UnsupportedCitation) -> evidence record
/// The caller comes from the same PermissionDirectory as the reimbursement desk;
/// which employee may read which document is the next chapter's step.
/// </summary>
public sealed partial class FaqDesk(
    IChatClient model, FaqSearchService search, PermissionDirectory directory, string handbookTenant)
{
    public const string Abstention = FaqAgentFactory.Abstention;
    public const string Withheld =
        "I found handbook passages for this question but could not produce an answer backed by them. Please contact HR.";
    public const string NotAnEmployee = "This assistant answers questions from Contoso employees only.";

    private readonly ChatClientAgent _agent = new(model, new ChatClientAgentOptions
    {
        Name = "ContosoFaqDesk",
        ChatOptions = new ChatOptions { Instructions = FaqAgentFactory.Instructions, Temperature = 0.2f, MaxOutputTokens = 400 },
    });
    private readonly ConcurrentQueue<FaqReply> _log = new();

    /// <summary>Every reply with its evidence, in the order they finished.</summary>
    public IReadOnlyList<FaqReply> EvidenceLog => [.. _log];

    public async Task<FaqReply> AskAsync(string tenant, string subject, string question,
        CancellationToken cancellationToken = default)
    {
        Caller caller = directory.Authenticate(tenant, subject);
        Guid id = Guid.NewGuid();
        if (caller.TenantId != handbookTenant)
            return Record(new(id, caller.TenantId, caller.SubjectId, question, FaqOutcome.NotPermitted, NotAnEmployee, [], []));

        IReadOnlyList<FaqHit> hits = await search.RetrieveAsync(question, cancellationToken);
        EvidenceItem[] evidence = [.. hits.Select(h => new EvidenceItem(h.ChunkId, h.Source, Math.Round(h.Score, 4)))];
        if (hits.Count == 0)
            return Record(new(id, caller.TenantId, caller.SubjectId, question, FaqOutcome.NoEvidence, Abstention, [], []));

        // One call, no session: the excerpts and the question travel together,
        // so concurrent questions cannot see each other's evidence.
        AgentResponse response = await _agent.RunAsync(
            [new ChatMessage(ChatRole.User, FaqContextProvider.FormatEvidence(hits)), new ChatMessage(ChatRole.User, question)],
            cancellationToken: cancellationToken);
        string text = response.Text.Trim();
        string[] cited = [.. Citation().Matches(text).Select(m => m.Groups[1].Value).Distinct()];

        FaqOutcome outcome =
            text.Contains(Abstention, StringComparison.OrdinalIgnoreCase) ? FaqOutcome.Declined
            : cited.Length == 0 ? FaqOutcome.Uncited
            : cited.Any(c => evidence.All(e => e.Source != c)) ? FaqOutcome.UnsupportedCitation
            : FaqOutcome.Answered;
        string shown = outcome switch
        {
            FaqOutcome.Answered => text,
            FaqOutcome.Declined => Abstention,
            _ => Withheld,
        };
        return Record(new(id, caller.TenantId, caller.SubjectId, question, outcome, shown, evidence, cited));
    }

    private FaqReply Record(FaqReply reply)
    {
        _log.Enqueue(reply);
        return reply;
    }

    [GeneratedRegex(@"\[(?:Source:\s*)?([a-z0-9-]+)\]")]   // [pto-policy] or the excerpt tag [Source: pto-policy]
    private static partial Regex Citation();
}
