using Ch08.ActionPolicy;
using Ch08.IdempotentCommit;

namespace Ch18.ApprovalUi;

/// <summary>--self-test: the approval desk on a temporary SQLite file, offline.</summary>
internal static class ApprovalSelfTest
{
    private static int _passed, _total;

    public static Task<int> RunAsync()
    {
        Console.WriteLine("=== Ch18.ApprovalUi (self-test, offline) ===\n");
        string db = Path.Combine(Path.GetTempPath(), $"ch18-approvalui-selftest-{Guid.NewGuid():N}.db");
        DateTimeOffset now = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
        Dictionary<string, HashSet<string>> rights = new()
        {
            ["E-1001"] = [Permissions.SubmitReimbursement, Roles.FinanceReviewer],   // alice also reviews (v4's people)
            ["E-2002"] = [Permissions.SubmitReimbursement],
            ["rita"] = [Roles.FinanceReviewer],
            ["ana"] = [Roles.FinanceReviewer],
        };
        IReadOnlySet<string> RightsOf(string tenant, string subject)
        {
            lock (rights) return tenant == "contoso" && rights.TryGetValue(subject, out HashSet<string>? r) ? [.. r] : new HashSet<string>();
        }
        try
        {
            ApprovalDesk desk = new(db, RightsOf, () => now);
            desk.Commits.SeedAccount(new EmployeeAccount("contoso", "E-1001", "EUR", 500m, 7));
            Caller alice = new("contoso", "E-1001", RightsOf("contoso", "E-1001"));
            Guid op = Guid.NewGuid();
            Guid id = desk.Propose("contoso", "E-1001", new ReimbursementProposal(op, "E-1001", 120m, "EUR", 7));

            Console.WriteLine("-- 1. Who sees the review screen");
            ReviewView? v = desk.View("contoso", "rita", id);
            Check($"rita (finance reviewer): version {v?.Version}, {v?.Amount} {v?.Currency}, digest {v?.Digest[..8]}...", v is { Version: 1, Amount: 120m });
            Check("bob (no reviewer role): not found", desk.View("contoso", "E-2002", id) is null);
            Check("fabrikam/rita (same subject, other tenant): not found", desk.View("fabrikam", "rita", id) is null);
            DecisionRequest approve = new(v!.Version, v.Digest, "approve");

            Console.WriteLine("\n-- 2. Who may decide");
            Check($"alice on her own claim: {desk.Decide("contoso", "E-1001", id, approve)}", desk.Decide("contoso", "E-1001", id, approve) == new DecisionResult(403, "separation_of_duties"));
            Check($"bob: {desk.Decide("contoso", "E-2002", id, approve)}", desk.Decide("contoso", "E-2002", id, approve) == new DecisionResult(403, "reviewer_not_authorized"));
            Check($"fabrikam/rita: {desk.Decide("fabrikam", "rita", id, approve)}", desk.Decide("fabrikam", "rita", id, approve).Status == 404);
            string digestOf12 = ReimbursementPolicy.Digest("contoso", "E-1001", new ReimbursementProposal(op, "E-1001", 12m, "EUR", 7));
            Check("a digest for 12 EUR on the 120 EUR proposal: 409 digest_mismatch",
                desk.Decide("contoso", "rita", id, approve with { Digest = digestOf12 }) == new DecisionResult(409, "digest_mismatch"));

            Console.WriteLine("\n-- 3. The double click");
            int naive = NaiveDoubleClick(db + ".naive");
            Check($"DEFECT REPRODUCED: check-then-insert with no key per version, two clicks on Task.Run: {naive} approvals stored", naive == 2);
            Task<DecisionResult> first = Task.Run(() => desk.Decide("contoso", "rita", id, approve));
            Task<DecisionResult> second = Task.Run(() => desk.Decide("contoso", "rita", id, approve));
            DecisionResult[] both = [first.Result, second.Result];
            Check($"rita approves twice at once (Task.Run): {both[0]} / {both[1]}",
                both.Count(r => r == new DecisionResult(200, "Approved")) == 1 && both.Count(r => r == new DecisionResult(200, "Approved", true)) == 1);
            Check($"the first response is lost and rita retries: {desk.Decide("contoso", "rita", id, approve)}; stored decisions: {desk.DecisionCount(id)}",
                desk.Decide("contoso", "rita", id, approve).Replayed && desk.DecisionCount(id) == 1);
            Check($"ana rejects the decided version: {desk.Decide("contoso", "ana", id, approve with { Decision = "reject" })}",
                desk.Decide("contoso", "ana", id, approve with { Decision = "reject" }) == new DecisionResult(409, "already_decided"));

            Console.WriteLine("\n-- 4. The commit is ch. 8's policy and idempotent commit");
            (PolicyResult policy, CommitResult? commit) = desk.Commit(id, alice);
            Check($"policy {policy.Decision} ({policy.Reason}); {commit?.Status}: {commit?.Outcome}",
                policy.Reason == "exact_proposal_approved" && commit?.Status == CommitStatus.Committed);
            (_, CommitResult? again) = desk.Commit(id, alice);
            Check($"commit again: {again?.Status}, the stored outcome", again?.Status == CommitStatus.Replayed);
            Check("a committed proposal cannot be revised", !desk.Revise(id, new ReimbursementProposal(op, "E-1001", 300m, "EUR", 8), out _));

            Console.WriteLine("\n-- 5. Revise is not reject; a changed argument makes the old approval stale");
            Guid op2 = Guid.NewGuid();
            Guid p2 = desk.Propose("contoso", "E-1001", new ReimbursementProposal(op2, "E-1001", 200m, "EUR", 8));
            ReviewView v2 = desk.View("contoso", "rita", p2)!;
            Check($"rita asks for a revision: {desk.Decide("contoso", "rita", p2, new DecisionRequest(1, v2.Digest, "revise", "receipt shows 180 EUR"))}",
                desk.View("contoso", "rita", p2)!.Status == nameof(ProposalStatus.RevisionRequested));
            Check($"while a revision is pending the policy says {desk.Commit(p2, alice).Policy.Reason}", desk.Commit(p2, alice).Commit is null);
            bool revised = desk.Revise(p2, new ReimbursementProposal(op2, "E-1001", 180m, "EUR", 8), out int newVersion);
            ReviewView v3 = desk.View("contoso", "rita", p2)!;
            Check($"the application revises to 180 EUR: version {newVersion}, new digest", revised && v3.Version == 2 && v3.Digest != v2.Digest);
            Check($"rita's old approval (version 1, old digest) replayed: {desk.Decide("contoso", "rita", p2, new DecisionRequest(1, v2.Digest, "approve"))}",
                desk.Decide("contoso", "rita", p2, new DecisionRequest(1, v2.Digest, "approve")) == new DecisionResult(409, "stale_version"));
            Check($"rita approves version 2: {desk.Decide("contoso", "rita", p2, new DecisionRequest(2, v3.Digest, "approve"))}; commit {desk.Commit(p2, alice).Commit?.Outcome}",
                desk.View("contoso", "rita", p2)!.Status == nameof(ProposalStatus.Approved));
            Guid p3 = desk.Propose("contoso", "E-1001", new ReimbursementProposal(Guid.NewGuid(), "E-1001", 90m, "EUR", 9));
            ReviewView v4 = desk.View("contoso", "rita", p3)!;
            desk.Decide("contoso", "rita", p3, new DecisionRequest(1, v4.Digest, "reject", "not a business trip"));
            Check("a rejected proposal stays rejected: no revision, no commit",
                !desk.Revise(p3, new ReimbursementProposal(Guid.NewGuid(), "E-1001", 80m, "EUR", 9), out _)
                && desk.Commit(p3, alice).Policy.Reason == "approval_rejected");

            Console.WriteLine("\n-- 6. What a version check prevents");
            HashSet<Guid> approvedIds = [p2];   // an approval stored by proposal id alone
            ReimbursementProposal raised = new(op2, "E-1001", 300m, "EUR", 8);
            Check("DEFECT REPRODUCED: keyed by proposal id, rita's approval of 180 EUR also covers 300 EUR", approvedIds.Contains(p2));
            ApprovalRecord of180 = new(v3.Digest, "contoso", "E-1001", "rita", Roles.FinanceReviewer, ReimbursementPolicy.Version, now.AddHours(24), true);
            PolicyResult bound = ReimbursementPolicy.Evaluate(alice, new EmployeeAccount("contoso", "E-1001", "EUR", 500m, 8), raised, of180, now);
            Check($"bound to the digest, the same approval for 300 EUR: {bound.Decision} ({bound.Reason})", bound.Reason == "approval_invalid");

            Console.WriteLine("\n-- 7. A reviewer revoked mid-review, an expired proposal");
            Guid p4 = desk.Propose("contoso", "E-1001", new ReimbursementProposal(Guid.NewGuid(), "E-1001", 70m, "EUR", 9));
            ReviewView v5 = desk.View("contoso", "ana", p4)!;   // ana opened the screen
            lock (rights) rights["ana"].Remove(Roles.FinanceReviewer);
            Check($"ana's role is revoked, then she posts: {desk.Decide("contoso", "ana", p4, new DecisionRequest(1, v5.Digest, "approve"))}",
                desk.Decide("contoso", "ana", p4, new DecisionRequest(1, v5.Digest, "approve")) == new DecisionResult(403, "reviewer_not_authorized"));
            now = now.AddHours(25);
            Check($"25 hours later rita approves: {desk.Decide("contoso", "rita", p4, new DecisionRequest(1, v5.Digest, "approve"))}",
                desk.Decide("contoso", "rita", p4, new DecisionRequest(1, v5.Digest, "approve")) == new DecisionResult(410, "expired"));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(db); } catch (IOException) { }
        }
        Console.WriteLine($"\nRESULT {_passed}/{_total}");
        return Task.FromResult(_passed == _total ? 0 : 1);
    }

    // The first version of the POST: look for a decision, then insert one. Both
    // clicks look before either inserts (a barrier makes the interleaving certain).
    private static int NaiveDoubleClick(string path)
    {
        using (Microsoft.Data.Sqlite.SqliteConnection setup = new($"Data Source={path}"))
        {
            setup.Open();
            using Microsoft.Data.Sqlite.SqliteCommand create = setup.CreateCommand();
            create.CommandText = "CREATE TABLE decisions (proposal_id TEXT, reviewer TEXT, decision TEXT)";
            create.ExecuteNonQuery();
        }
        using Barrier bothLooked = new(2);
        Task[] clicks = [.. Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            using Microsoft.Data.Sqlite.SqliteConnection c = new($"Data Source={path};Default Timeout=30");
            c.Open();
            using Microsoft.Data.Sqlite.SqliteCommand look = c.CreateCommand();
            look.CommandText = "SELECT COUNT(*) FROM decisions WHERE proposal_id = 'p1'";
            long found = (long)look.ExecuteScalar()!;
            bothLooked.SignalAndWait();
            if (found == 0)
            {
                using Microsoft.Data.Sqlite.SqliteCommand insert = c.CreateCommand();
                insert.CommandText = "INSERT INTO decisions VALUES ('p1', 'rita', 'approve')";
                insert.ExecuteNonQuery();
            }
        }))];
        Task.WaitAll(clicks);
        using Microsoft.Data.Sqlite.SqliteConnection read = new($"Data Source={path}");
        read.Open();
        using Microsoft.Data.Sqlite.SqliteCommand count = read.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM decisions";
        int stored = Convert.ToInt32(count.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        read.Close();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(path);
        return stored;
    }

    private static void Check(string name, bool ok)
    {
        _total++;
        if (ok) _passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    }
}
