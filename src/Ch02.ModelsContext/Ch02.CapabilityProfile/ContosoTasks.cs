namespace Ch02.CapabilityProfile;

/// <summary>One policy question with the answer the policy text implies.</summary>
public sealed record ProfileTask(string Id, string Question, string Expected);

public static class ContosoTasks
{
    // The policy as the authorized-actions chapter implements it
    // (Ch08.ActionPolicy.ReimbursementPolicy): low-value limit 50, approval by
    // a finance reviewer above it, no self-approval, currency must match.
    public const string Policy = """
        Contoso reimbursement policy (reimbursement-v1):
        - A claim of 50.00 or less in the account's currency is allowed without review.
        - A claim above 50.00 needs approval by a finance reviewer.
        - The reviewer must not be the employee who filed the claim.
        - A claim whose currency differs from the account's currency is denied.
        """;

    public const string Instructions = """
        You decide Contoso expense claims from the policy below and nothing else.
        Reply with JSON only: {"decision":"allow"} or {"decision":"review"} or {"decision":"deny"}.
        """;

    public static readonly ProfileTask[] All =
    [
        new("taxi-42", "Taxi fare 42.00 EUR, employee account in EUR.", "allow"),
        new("taxi-64", "Taxi fare 64.80 EUR, employee account in EUR, no approval yet.", "review"),
        new("limit-50", "Train ticket 50.00 EUR, employee account in EUR.", "allow"),
        new("self-approval", "Hotel 450.00 EUR, account in EUR. The approver is the employee who filed the claim.", "deny"),
        new("currency", "Hotel 180.00 USD, employee account in EUR.", "deny"),
    ];
}
