// ============================================================================
// Ch18.ApprovalUi: approval as a reviewable decision.
// ----------------------------------------------------------------------------
//   GET  /reviews/{id}            the exact arguments, the version, the digest
//   POST /reviews/{id}/decision   {"version":1,"digest":"...","decision":
//                                  "approve"|"reject"|"revise","note":"..."}
// The server loads the proposal and checks: tenant (404), reviewer role now
// (403), not the requester (403), latest version (409 stale_version), digest
// (409 digest_mismatch), an existing decision (200 replayed / 409
// already_decided), expiry (410). Reject ends the proposal; revise asks for
// a new version. The commit is ch. 8's policy and idempotent commit.
//
//   dotnet run                 a scripted review, offline
//   dotnet run -- --self-test  every rule, the double click on Task.Run,
//                              a revoked reviewer, a revised proposal (RESULT n/n)
// Simulated effects only: nothing is paid.
// ============================================================================

using Ch08.ActionPolicy;
using Ch18.ApprovalUi;

if (args.Contains("--self-test")) return await ApprovalSelfTest.RunAsync();

string db = Path.Combine(Path.GetTempPath(), $"ch18-approvalui-{Guid.NewGuid():N}.db");
Dictionary<string, HashSet<string>> rights = new()
{
    ["E-1001"] = [Permissions.SubmitReimbursement],
    ["rita"] = [Roles.FinanceReviewer],
};
ApprovalDesk desk = new(db, (tenant, subject) => tenant == "contoso" && rights.TryGetValue(subject, out var r) ? r : [],
    () => DateTimeOffset.UtcNow);
desk.Commits.SeedAccount(new EmployeeAccount("contoso", "E-1001", "EUR", 500m, 7));
Guid id = desk.Propose("contoso", "E-1001", new ReimbursementProposal(Guid.NewGuid(), "E-1001", 120m, "EUR", 7));

ReviewView view = desk.View("contoso", "rita", id)!;
Console.WriteLine($"rita's review screen: {view.Amount} {view.Currency} for {view.Requester}, version {view.Version}, digest {view.Digest[..12]}...");
Console.WriteLine($"rita approves:             {desk.Decide("contoso", "rita", id, new DecisionRequest(view.Version, view.Digest, "approve"))}");
Console.WriteLine($"the response is lost, rita clicks again: {desk.Decide("contoso", "rita", id, new DecisionRequest(view.Version, view.Digest, "approve"))}");
(PolicyResult policy, Ch08.IdempotentCommit.CommitResult? commit) = desk.Commit(id,
    new Caller("contoso", "E-1001", rights["E-1001"]));
Console.WriteLine($"commit: policy {policy.Decision} ({policy.Reason}); {commit?.Status}: {commit?.Outcome}");
Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
File.Delete(db);
return 0;
