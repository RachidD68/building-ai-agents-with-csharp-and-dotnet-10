// ============================================================================
// Idempotent commit: one effect per business operation
// ----------------------------------------------------------------------------
// Approval authorizes an operation; it does not stop the operation from
// running twice. A double-clicked Submit, a client retry after a timeout or a
// resumed workflow can all send the same reimbursement again. The fix lives in
// the application's database, in ONE transaction:
//
//   1. claim the business key: UNIQUE (tenant, operation_id), with a digest of
//      the arguments. Same key + same arguments -> the stored outcome.
//      Same key + other arguments -> a conflict.
//   2. recheck the account version (and balance) inside the transaction;
//   3. change the state, store the outcome and write an outbox row for the
//      external effect, then commit. All of it, or none of it.
//
// No model. SQLite file in a temporary folder, deleted at the end.
//   dotnet run -- --self-test      prints RESULT n/n, exit code 1 on any failure
// ============================================================================

using System.Collections.Concurrent;
using Ch08.ActionPolicy;
using Ch08.IdempotentCommit;

bool selfTest = args.Contains("--self-test");
Console.WriteLine(selfTest
    ? "=== Idempotent commit: self-test ==="
    : "=== Idempotent commit (no model; the same suite runs with --self-test) ===");
return await CommitSuite.RunAsync();

internal static class CommitSuite
{
    const string Tenant = "contoso";
    static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    static int s_passed, s_total;

    public static async Task<int> RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "ch08-idempotent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new OperationStore(Path.Combine(root, "app.db"));
            store.Initialize();
            store.SeedAccount(new EmployeeAccount(Tenant, "E-1001", "EUR", 500m, 7));
            var proposal = new ReimbursementProposal(
                Guid.Parse("7d5f2a51-3c1e-4f0a-9a52-2a8d7c8e4b10"), "E-1001", 100m, "EUR", 7);

            // 1. Fifty identical submits at once, each on its own connection.
            Console.WriteLine("\n-- 50 concurrent identical submits --");
            int inFlight = 0, maxInFlight = 0;
            ConcurrentBag<CommitResult> results = [];
            using var start = new ManualResetEventSlim(false);
            Task[] submits = Enumerable.Range(0, 50).Select(_ => Task.Run(() =>
            {
                start.Wait();
                int now = Interlocked.Increment(ref inFlight);
                InterlockedMax(ref maxInFlight, now);
                results.Add(store.Submit(Tenant, "alice", proposal, Now));
                Interlocked.Decrement(ref inFlight);
            })).ToArray();
            start.Set();
            await Task.WhenAll(submits);

            int committed = results.Count(r => r.Status == CommitStatus.Committed);
            int replayed = results.Count(r => r.Status == CommitStatus.Replayed);
            Console.WriteLine($"  committed={committed} replayed={replayed} other={50 - committed - replayed} " +
                              "(50 connections opened)");
            Expect("the submits overlapped on two or more connections", maxInFlight >= 2);
            Expect("exactly one submit committed", committed == 1);
            Expect("the other 49 replayed the stored outcome", replayed == 49 &&
                results.Select(r => r.Outcome).Distinct().Count() == 1);
            EmployeeAccount? after = store.LoadAccount(Tenant, "E-1001");
            Expect($"one state change: balance {after?.ReimbursableBalance}, version {after?.Version}",
                after is { ReimbursableBalance: 400m, Version: 8 });
            Expect("one operations row and one outbox row",
                store.Count("operations") == 1 && store.Count("outbox") == 1);

            // 2. The same submit later, from a fresh store instance (a restart).
            Console.WriteLine("\n-- Replay, conflict and stale version --");
            CommitResult later = new OperationStore(store.DatabasePath).Submit(Tenant, "alice", proposal, Now.AddHours(1));
            Expect($"after a restart, the same submit -> {later.Status}: {later.Outcome}",
                later.Status == CommitStatus.Replayed && later.Outcome == results.First().Outcome);
            Expect("100.00m replays the operation committed as 100m",
                store.Submit(Tenant, "alice", proposal with { Amount = 100.00m }, Now).Status == CommitStatus.Replayed);

            // 3. The same key with other arguments.
            CommitResult conflict = store.Submit(Tenant, "alice", proposal with { Amount = 120m }, Now);
            Expect($"same operation id, amount 120 -> {conflict.Status}", conflict.Status == CommitStatus.Conflict);
            CommitResult otherRequester = store.Submit(Tenant, "mallory", proposal, Now);
            Expect($"same operation id, another requester -> {otherRequester.Status}",
                otherRequester.Status == CommitStatus.Conflict);

            // 4. A new operation built against version 7, but the account is at 8.
            var stale = proposal with { OperationId = Guid.Parse("0b1e8a3e-5d4c-4b7f-8e2a-6f3d9c1a7e55") };
            CommitResult staleResult = store.Submit(Tenant, "alice", stale, Now);
            Expect($"stale version (expected 7, stored 8) -> {staleResult.Status}",
                staleResult.Status == CommitStatus.StaleVersion);
            Expect("the stale submit left no trace: same balance, version, operations and outbox",
                store.LoadAccount(Tenant, "E-1001") is { ReimbursableBalance: 400m, Version: 8 } &&
                store.Count("operations") == 1 && store.Count("outbox") == 1);

            // 5. A new operation at the current version is a new reimbursement.
            CommitResult second = store.Submit(Tenant, "alice", stale with { ExpectedVersion = 8 }, Now);
            Expect($"new operation id at version 8 -> {second.Status}: {second.Outcome}",
                second.Status == CommitStatus.Committed &&
                store.LoadAccount(Tenant, "E-1001") is { ReimbursableBalance: 300m, Version: 9 } &&
                store.Count("outbox") == 2);

            // 6. The balance is rechecked inside the transaction too.
            var tooMuch = new ReimbursementProposal(Guid.Parse("e2c4d6f8-1a3b-4c5d-8e9f-0a1b2c3d4e5f"),
                "E-1001", 301m, "EUR", 9);
            CommitResult over = store.Submit(Tenant, "alice", tooMuch, Now);
            Expect($"301 against a balance of 300 -> {over.Status}, no effect",
                over.Status == CommitStatus.InsufficientBalance &&
                store.LoadAccount(Tenant, "E-1001") is { ReimbursableBalance: 300m, Version: 9 } &&
                store.Count("operations") == 2);

            Console.WriteLine($"\nRESULT {s_passed}/{s_total}");
            return s_passed == s_total ? 0 : 1;
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)) &&
               Interlocked.CompareExchange(ref target, value, current) != current) { }
    }

    static void Expect(string name, bool ok)
    {
        s_total++;
        if (ok) s_passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    }
}
