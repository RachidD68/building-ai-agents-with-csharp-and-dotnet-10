using System.Text.RegularExpressions;

namespace Ch11.AuthorizedSearch;

/// <summary>Why an answer is not delivered as it is. Each reason asks for a different fix.</summary>
public static class AbstentionReason
{
    public const string NoAuthorizedEvidence = "no_authorized_evidence"; // the search returned nothing for this caller
    public const string NoCitation = "no_citation";                       // the answer cites nothing
    public const string CitationNotSupplied = "citation_not_supplied";    // it cites an id this request never received
}

public sealed record CheckedAnswer(
    string Text, IReadOnlyList<string> Cited, IReadOnlyList<string> Rejected, string? Abstention)
{
    public bool Accepted => Abstention is null;
}

/// <summary>
/// The deterministic half of grounding: every cited id must be one the ledger of
/// this request holds. Whether the passage supports the claim is a separate,
/// semantic check (the evaluation chapter); this one only proves where text came from.
/// Every id-shaped token counts, bracketed or not, in a list or in parentheses, in any
/// case: a planted id must not get past the check by changing its punctuation.
/// </summary>
public static partial class EvidenceCheck
{
    public static CheckedAnswer Check(string answer, EvidenceLedger ledger)
    {
        string[] cited = [.. IdToken().Matches(answer).Select(m => m.Value.ToLowerInvariant()).Distinct()];
        string[] rejected = [.. cited.Where(id => !ledger.WasIssued(id))];

        string? abstention =
            ledger.Issued.Count == 0 ? AbstentionReason.NoAuthorizedEvidence
            : rejected.Length > 0 ? AbstentionReason.CitationNotSupplied
            : cited.Length == 0 ? AbstentionReason.NoCitation
            : null;
        return new CheckedAnswer(answer, cited, rejected, abstention);
    }

    [GeneratedRegex(@"[a-z0-9-]+@v\d+#\d+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IdToken();
}
