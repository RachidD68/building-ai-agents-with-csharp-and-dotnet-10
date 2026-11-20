using Ch08.UncertainOutcome;

namespace Ch15.ExternalEffects;

/// <summary>
/// --self-test, in process and offline. Each case gets a fresh store folder.
/// A "replay" here is a second run from the checkpoint taken before the
/// payout step, which is exactly what a worker killed inside the step does
/// next; Ch15.RestartTests kills real processes at each point.
/// </summary>
internal static class ExternalEffectsSelfTest
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);

    public static async Task<int> RunAsync()
    {
        Console.WriteLine("=== External Effects: self-test (in process, offline) ===");
        Tally t = new();
        string root = Path.Combine(Path.GetTempPath(), $"external-effects-selftest-{Guid.NewGuid():N}");
        try
        {
            Console.WriteLine("\n-- 1. Pay, then run the payout step again from the restart point");
            PayoutHost h = Host(root, "replay", out PayoutRow row);
            PayoutResult? first = await h.RunAsync(row.ClaimId, "worker-a", Lease, new NoHook());
            PayoutResult? replay = await h.RunAsync(row.ClaimId, "worker-b", Lease, new NoHook());
            IReadOnlyList<(string Reference, string PaymentId)> journal = Journal(h);
            t.Check("first run: Paid, one effect, reference contoso:<operation id>",
                first?.Status == "Paid" && journal.Count == 1 && journal[0].Reference == $"contoso:{row.OperationId:D}");
            t.Check("replay re-reads the operation record: still Paid, no provider call, still one effect",
                replay?.Status == "Paid" && replay.Detail.StartsWith("re-read the operation record", StringComparison.Ordinal) &&
                Journal(h).Count == 1);
            t.Check("never a new operation id: the record and the result keep the approval's id",
                h.Store.Load(row.ClaimId)!.OperationId == row.OperationId && replay!.OperationId == row.OperationId);

            PayoutHost naive = Host(root, "naive", out PayoutRow naiveRow);
            await naive.RunAsync(naiveRow.ClaimId, "worker-a", Lease, new NoHook());
            PayoutResult? twice = await naive.RunAsync(naiveRow.ClaimId, "worker-b", Lease, new NoHook(), naive: true);
            IReadOnlyList<(string Reference, string PaymentId)> paidTwice = Journal(naive);
            t.Defect("the naive step replayed with a fresh operation id pays twice (two references)",
                twice?.Status == "Paid" && paidTwice.Count == 2 && paidTwice[0].Reference != paidTwice[1].Reference);

            Console.WriteLine("\n-- 2. Cancel: before the commit, after the payout, and during an uncertain call");
            PayoutHost early = Host(root, "cancel-before", out PayoutRow earlyRow);
            PayoutResult? cancelled = await early.RunAsync(earlyRow.ClaimId, "worker-a", Lease,
                new ActionHook(StepPoints.BeforeCommit, () => { early.Store.RequestCancel(earlyRow.ClaimId); return default; }));
            t.Check("cancel before the commit -> Cancelled, nothing sent (0 effects)",
                cancelled?.Status == "Cancelled" && Journal(early).Count == 0);
            PayoutResult? afterCancel = await early.RunAsync(earlyRow.ClaimId, "worker-b", Lease, new NoHook());
            t.Check("a replay of a cancelled payout stays Cancelled", afterCancel?.Status == "Cancelled" && Journal(early).Count == 0);

            PayoutHost late = Host(root, "cancel-after", out PayoutRow lateRow);
            await late.RunAsync(lateRow.ClaimId, "worker-a", Lease, new NoHook());
            late.Store.RequestCancel(lateRow.ClaimId);
            PayoutResult? tooLate = await late.RunAsync(lateRow.ClaimId, "worker-b", Lease, new NoHook());
            t.Check("cancel after the payout completed -> still Paid, one effect; cancelling is not undoing",
                tooLate?.Status == "Paid" && tooLate.Detail.Contains("cancelling is not undoing") && Journal(late).Count == 1);

            PayoutHost unsure = Host(root, "cancel-during", out PayoutRow unsureRow);
            PayoutResult? unknown = await unsure.RunAsync(unsureRow.ClaimId, "worker-a", Lease,
                new ActionHook(StepPoints.BeforeEffect, () => { unsure.Store.RequestCancel(unsureRow.ClaimId); return default; }),
                ProviderFault.AcceptThenTimeout);
            t.Check("cancel during a call whose reply is lost -> Unknown, and the journal already holds the payment",
                unknown?.Status == "Unknown" && unsure.Store.Load(unsureRow.ClaimId)!.State == "Unknown" && Journal(unsure).Count == 1);
            PayoutResult? settled = await unsure.RunAsync(unsureRow.ClaimId, "worker-b", Lease, new NoHook());
            t.Check("resume reconciles from the restored workflow: found by reference -> Paid, one effect, cancel too late",
                settled?.Status == "Paid" && settled.Detail.Contains("found") && settled.Detail.Contains("by reference") &&
                settled.Detail.Contains("cancelling is not undoing") && Journal(unsure).Count == 1);

            Console.WriteLine("\n-- 3. A request that never arrived");
            PayoutHost lost = Host(root, "lost", out PayoutRow lostRow);
            PayoutResult? lostFirst = await lost.RunAsync(lostRow.ClaimId, "worker-a", Lease, new NoHook(), ProviderFault.LostRequest);
            t.Check("lost request -> Unknown, nothing in the journal", lostFirst?.Status == "Unknown" && Journal(lost).Count == 0);
            PayoutResult? resent = await lost.RunAsync(lostRow.ClaimId, "worker-b", Lease, new NoHook());
            t.Check("resume: not found by reference, sent again with the SAME reference -> Paid, one effect",
                resent?.Status == "Paid" && resent.Detail.Contains("sent again") &&
                Journal(lost) is [var only] && only.Reference == $"contoso:{lostRow.OperationId:D}");

            Console.WriteLine("\n-- 4. Two workers, one claim");
            DateTimeOffset now = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
            string folder = Path.Combine(root, "stale");
            PayoutHost a = new(folder, () => now), b = new(folder, () => now);
            PayoutRow staleRow = a.Store.Approve("CLM-2001", PayoutHost.Tenant, "E-1001", 64.80m, "EUR");
            PayoutResult? takeover = null;
            PayoutResult? stale = await a.RunAsync(staleRow.ClaimId, "worker-a", Lease, new ActionHook(StepPoints.BeforeCommit, async () =>
            {
                now += TimeSpan.FromSeconds(31);                // worker A stalls past its lease ...
                takeover = await b.RunAsync(staleRow.ClaimId, "worker-b", Lease, new NoHook());   // ... B takes over
            }), renew: false);
            t.Check("B takes the expired lease (token 2) and pays", takeover?.Status == "Paid" && a.Store.LoadLease(staleRow.ClaimId)!.Token == 2);
            t.Check("A wakes with token 1: StaleLease, nothing committed, still one effect",
                stale?.Status == "StaleLease" && Journal(a).Count == 1 && a.Store.Load(staleRow.ClaimId)!.Fence == 2);

            PayoutHost held = Host(root, "held", out PayoutRow heldRow);
            held.Store.TryAcquireLease(heldRow.ClaimId, "someone-else", DateTimeOffset.UtcNow, Lease);
            t.Check("an unexpired lease held by another worker -> this worker does nothing",
                await held.RunAsync(heldRow.ClaimId, "worker-a", Lease, new NoHook()) is null && Journal(held).Count == 0);

            PayoutHost race = Host(root, "race", out PayoutRow raceRow);
            PayoutResult?[] racers = await Task.WhenAll(
                Task.Run(() => race.RunAsync(raceRow.ClaimId, "worker-a", Lease, new NoHook())),
                Task.Run(() => race.RunAsync(raceRow.ClaimId, "worker-b", Lease, new NoHook())));
            // Which one loses depends on timing: it finds the lease held, or (if the winner already
            // finished and released it) takes the lease and re-reads a Paid record. Never a second payment.
            t.Check("two workers started at once (Task.Run): one pays; the other finds the lease held or re-reads; one effect",
                racers.Count(r => r?.Detail.StartsWith("paid", StringComparison.Ordinal) == true) == 1 &&
                racers.Count(r => r is null || r.Detail.StartsWith("re-read", StringComparison.Ordinal)) == 1 &&
                Journal(race).Count == 1);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return t.Finish();
    }

    private static PayoutHost Host(string root, string name, out PayoutRow row)
    {
        PayoutHost host = new(Path.Combine(root, name));
        row = host.Store.Approve("CLM-2001", PayoutHost.Tenant, "E-1001", 64.80m, "EUR");
        return host;
    }

    private static IReadOnlyList<(string Reference, string PaymentId)> Journal(PayoutHost host) => PayoutStore.ReadJournal(host.JournalPath);
}

internal sealed class Tally
{
    private int _passed, _total;

    public void Check(string name, bool ok)
    {
        _total++;
        if (ok) _passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    }

    public void Defect(string name, bool reproduced) => Check($"DEFECT REPRODUCED: {name}", reproduced);

    public int Finish()
    {
        Console.WriteLine($"\nRESULT {_passed}/{_total}");
        return _passed == _total ? 0 : 1;
    }
}
