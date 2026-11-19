// ============================================================================
// Action policy: may this caller commit this exact reimbursement?
// ----------------------------------------------------------------------------
// The model proposes a reimbursement (an employee, an amount, a currency). The
// application decides, with a deterministic policy over state it trusts:
//
//   permission -> scope -> operation id -> amount (> 0 and <= reimbursable
//   balance) -> currency -> version -> low-value Allow -> approval
//
// The result is Deny, RequireApproval or Allow, each with a reason code. An
// approval counts only when it was given by an authorized reviewer who is not
// the requester, for these exact values (canonical digest), under the current
// policy version, and before it expires.
//
// No model, no package, no API key: this program is its own test suite.
//   dotnet run -- --self-test      prints RESULT n/n, exit code 1 on any failure
// ============================================================================

using System.Globalization;
using Ch08.ActionPolicy;

bool selfTest = args.Contains("--self-test");
Console.WriteLine(selfTest
    ? "=== Action policy: self-test ==="
    : "=== Action policy (no model; the same suite runs with --self-test) ===");
return PolicySuite.Run();

internal static class PolicySuite
{
    static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    static readonly HashSet<string> CanSubmit = [Permissions.SubmitReimbursement];

    static int s_passed, s_total;

    public static int Run()
    {
        var alice = new Caller("contoso", "alice", CanSubmit);
        var account = new EmployeeAccount("contoso", "E-1001", "EUR", 500m, 7);
        var proposal = new ReimbursementProposal(
            Guid.Parse("c695f1c8-d181-499b-a506-0881f867b0fa"), "E-1001", 100m, "EUR", 7);
        ApprovalRecord approval = ApproveAs("rita", Roles.FinanceReviewer, alice, proposal);

        Console.WriteLine("\n-- Policy cases (decision and reason code) --");
        Check("exact proposal approved", Decision.Allow, "exact_proposal_approved", alice, account, proposal, approval);
        Check("missing approval", Decision.RequireApproval, "approval_missing", alice, account, proposal, null);
        Check("negative amount", Decision.Deny, "invalid_amount", alice, account, proposal with { Amount = -1m }, null);
        Check("zero amount", Decision.Deny, "invalid_amount", alice, account, proposal with { Amount = 0m }, null);
        Check("above reimbursable balance", Decision.Deny, "invalid_amount", alice, account, proposal with { Amount = 501m }, approval);
        Check("cross tenant", Decision.Deny, "resource_scope", alice with { TenantId = "fabrikam" }, account, proposal, approval);
        Check("permission revoked", Decision.Deny, "missing_permission", alice with { Permissions = new HashSet<string>() }, account, proposal, approval);
        Check("anonymous subject", Decision.Deny, "missing_permission", alice with { SubjectId = "" }, account, proposal, approval);
        Check("wrong employee", Decision.Deny, "resource_scope", alice, account, proposal with { EmployeeId = "E-2002" }, approval);
        Check("currency mismatch", Decision.Deny, "currency_mismatch", alice, account, proposal with { Currency = "USD" }, approval);
        Check("malformed currency", Decision.Deny, "currency_mismatch", alice, account, proposal with { Currency = "EU" }, approval);
        Check("currency symbol is not a code", Decision.Deny, "currency_mismatch", alice, account, proposal with { Currency = "€" }, approval);
        Check("stale resource version", Decision.Deny, "stale_version", alice, account with { Version = 8 }, proposal, approval);
        Check("approval expired", Decision.Deny, "approval_invalid", alice, account, proposal, approval with { ExpiresAtUtc = Now });
        Check("approval rejected", Decision.Deny, "approval_rejected", alice, account, proposal, approval with { Approved = false });
        Check("arguments changed after approval", Decision.Deny, "approval_invalid", alice, account, proposal with { Amount = 101m }, approval);
        Check("operation changed after approval", Decision.Deny, "approval_invalid", alice, account, proposal with { OperationId = Guid.NewGuid() }, approval);
        Check("policy version changed", Decision.Deny, "approval_invalid", alice, account, proposal, approval with { PolicyVersion = "reimbursement-v0" });
        Check("approval belongs to another requester", Decision.Deny, "approval_invalid", alice, account, proposal, approval with { RequesterId = "bob" });
        Check("missing operation id", Decision.Deny, "missing_operation_id", alice, account, proposal with { OperationId = Guid.Empty }, approval);
        Check("authorized low value", Decision.Allow, "low_value_policy", alice, account, proposal with { Amount = 50m }, null);
        Check("low value still requires permission", Decision.Deny, "missing_permission", alice with { Permissions = new HashSet<string>() }, account, proposal with { Amount = 20m }, null);

        Console.WriteLine("\n-- Reviewer identity and separation of duties --");
        Check("requester approved their own claim", Decision.Deny, "separation_of_duties", alice, account, proposal,
            ApproveAs("alice", Roles.FinanceReviewer, alice, proposal));
        Check("reviewer without the finance role", Decision.Deny, "reviewer_not_authorized", alice, account, proposal,
            ApproveAs("bob", "employee", alice, proposal));
        Check("approval with no reviewer identity", Decision.Deny, "reviewer_not_authorized", alice, account, proposal,
            approval with { ReviewerId = "" });

        Console.WriteLine("\n-- Canonical digest and currency form --");
        Expect("digest of 100m equals digest of 100.00m",
            ReimbursementPolicy.Digest("contoso", "alice", proposal) ==
            ReimbursementPolicy.Digest("contoso", "alice", proposal with { Amount = 100.00m }));
        Check("approval over 100m accepts the proposal written 100.00m", Decision.Allow, "exact_proposal_approved",
            alice, account, proposal with { Amount = 100.00m }, approval);
        Expect("digest is the same under de-DE, fr-FR and the invariant culture", DigestUnderCultures(proposal with { Amount = 1234.5m }));
        Check("currency ' eur ' is normalized to EUR", Decision.Allow, "exact_proposal_approved",
            alice, account, proposal with { Currency = " eur " }, approval);

        Console.WriteLine("\n-- Permission revoked between proposal and commit --");
        PolicyResult atProposal = ReimbursementPolicy.Evaluate(alice, account, proposal, null, Now);
        Caller atCommit = alice with { Permissions = new HashSet<string>() };   // reloaded from the auth context
        PolicyResult atCommitResult = ReimbursementPolicy.Evaluate(atCommit, account, proposal, approval, Now);
        Expect($"at proposal -> {atProposal.Reason}; at commit, with a valid approval -> {atCommitResult.Reason}",
            atProposal.Decision == Decision.RequireApproval &&
            atCommitResult is { Decision: Decision.Deny, Reason: "missing_permission" });

        // Defect reproductions. Each assertion PASSES when it shows that a
        // first-edition predicate accepts a bad input. They do not endorse the
        // old rules; they keep the counterexamples executable.
        Console.WriteLine("\n-- Defect reproductions (first-edition predicates) --");
        Expect("DEFECT REPRODUCED: substring grounding check accepts 115", "Employees get 115 days.".Contains("15"));
        Expect("DEFECT REPRODUCED: substring grounding check accepts a contradiction", "Not 15 days; employees get 20.".Contains("15"));
        Expect("DEFECT REPRODUCED: auto-approve rule amount <= 50 accepts -10", FirstEditionAutoApprove(-10m));
        Expect("DEFECT REPRODUCED: auto-approve rule amount <= 50 accepts 0", FirstEditionAutoApprove(0m));
        Expect("DEFECT REPRODUCED: a digest over the serialized decimal differs for 100m and 100.00m",
            System.Text.Json.JsonSerializer.Serialize(100m) != System.Text.Json.JsonSerializer.Serialize(100.00m));

        Console.WriteLine($"\nRESULT {s_passed}/{s_total}");
        return s_passed == s_total ? 0 : 1;
    }

    // The first edition's auto-approval predicate, kept only to reproduce its defect.
    static bool FirstEditionAutoApprove(decimal amount) => amount <= 50m;

    static ApprovalRecord ApproveAs(string reviewerId, string role, Caller requester, ReimbursementProposal p) =>
        new(ReimbursementPolicy.Digest(requester.TenantId, requester.SubjectId, p),
            requester.TenantId, requester.SubjectId, reviewerId, role,
            ReimbursementPolicy.Version, Now.AddMinutes(10), Approved: true);

    static bool DigestUnderCultures(ReimbursementProposal p)
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            HashSet<string> digests = [];
            foreach (string name in new[] { "de-DE", "fr-FR", "" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                digests.Add(ReimbursementPolicy.Digest("contoso", "alice", p));
            }
            return digests.Count == 1;
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    static void Check(string name, Decision expected, string expectedReason, Caller caller,
        EmployeeAccount account, ReimbursementProposal proposal, ApprovalRecord? approval)
    {
        PolicyResult actual = ReimbursementPolicy.Evaluate(caller, account, proposal, approval, Now);
        Expect($"{name} -> {actual.Decision} {actual.Reason}",
            actual.Decision == expected && actual.Reason == expectedReason);
    }

    static void Expect(string name, bool ok)
    {
        s_total++;
        if (ok) s_passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    }
}
