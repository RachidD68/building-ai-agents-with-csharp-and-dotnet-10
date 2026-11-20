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
/// </summary>
public static partial class EvidenceCheck
{
    public static CheckedAnswer Check(string answer, EvidenceLedger ledger)
    {
        string[] cited = [.. CitationPattern().Matches(answer).Select(m => m.Groups[1].Value).Distinct()];
        string[] rejected = [.. cited.Where(id => !ledger.WasIssued(id))];

        string? abstention =
            ledger.Issued.Count == 0 ? AbstentionReason.NoAuthorizedEvidence
            : rejected.Length > 0 ? AbstentionReason.CitationNotSupplied
            : cited.Length == 0 ? AbstentionReason.NoCitation
            : null;
        return new CheckedAnswer(answer, cited, rejected, abstention);
    }

    [GeneratedRegex(@"\[([a-z0-9-]+@v\d+#\d+)\]")]
    private static partial Regex CitationPattern();
}
