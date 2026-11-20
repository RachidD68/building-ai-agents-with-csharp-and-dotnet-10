// ============================================================================
// Restore drill: back up, lose, restore, reconcile, measure
// ----------------------------------------------------------------------------
// The durable stores (conversation metadata, approvals, delivery records,
// workflow state, configuration identity) live in one SQLite file. The drill
// backs it up, keeps working, loses it, and then:
//
//   check binary x schema x index ─> restore into an isolated folder
//     ─> verify (sha256, integrity_check, schema, identity, row counts)
//     ─> reconcile pending payments against the provider ─> promote
//
// and prints the recovery time (wall clock, measured) and the recovery point
// (incident time minus the newest write in the backup). The targets are lab
// choices. Everything runs on temporary storage and is deleted at the end.
//
//   dotnet run                    the drill, with its timeline
//   dotnet run -- --self-test     the drill twice (one and two reconcilers) and
//                                 its checks: RESULT n/n, exit code 1 on failure
// ============================================================================

using System.Globalization;
using Ch08.UncertainOutcome;
using Ch24.RestoreDrill;

bool selfTest = args.Contains("--self-test");
Console.WriteLine(selfTest ? "=== Restore drill: self-test ===" : "=== Restore drill (temporary storage) ===");
string root = Path.Combine(Path.GetTempPath(), "ch24-restore-" + Guid.NewGuid().ToString("N")[..8]);
try
{
    DrillReport r = await Drill.RunAsync(Path.Combine(root, "drill-1"), Console.WriteLine);
    Print(r);
    return selfTest ? await RestoreChecks.RunAsync(r, Path.Combine(root, "drill-2")) : 0;
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
}

static void Print(DrillReport r)
{
    Console.WriteLine("\n-- Restore --");
    foreach ((string attempt, RestoreResult result) in r.Attempts) Console.WriteLine($"  {attempt}: {result.Detail}");
    Console.WriteLine($"  binary {Drill.Current} into restore-c: {r.Restored.Detail}");
    Console.WriteLine("\n-- Reconcile --");
    foreach (Fate f in r.Reconcile.Fates) Console.WriteLine($"  {Drill.Label(f.Reference)}  {f.State,-10} {f.How}");
    Console.WriteLine("  uncertain (for a person):");
    foreach (Fate f in r.Reconcile.Uncertain) Console.WriteLine($"  {Drill.Label(f.Reference)}  {f.How}");
    Console.WriteLine("\n-- Measured --");
    Console.WriteLine($"  RTO {r.Rto.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture)} s (lab target {DrillReport.RtoTarget.TotalSeconds:0} s): incident declared -> restored, verified, reconciled, promoted");
    Console.WriteLine($"  RPO {r.Rpo.TotalMinutes:0} min (lab target {DrillReport.RpoTarget.TotalMinutes:0} min): incident {r.Incident:HH:mm} - newest write in the backup {r.Manifest.NewestWrite:HH:mm}");
    Console.WriteLine($"  binary {Drill.Current.Version}, schema {r.Manifest.Schema}, identity {r.Manifest.IdentityDigest}, " +
        $"index {r.Manifest.Identity["index_version"]} ({r.Manifest.Identity["embedding_model"]}), SQLite {r.Manifest.SqliteVersion}, " +
        $"Microsoft.Data.Sqlite {typeof(Microsoft.Data.Sqlite.SqliteConnection).Assembly.GetName().Version}");
}

internal static class RestoreChecks
{
    static int s_passed, s_total;

    public static async Task<int> RunAsync(DrillReport r, string secondRun)
    {
        Console.WriteLine("\n-- Checks --");
        Expect($"DEFECT REPRODUCED: File.Copy of the live file 'succeeded' ({r.FileCopy.Bytes} bytes, {r.FileCopy.Tables} tables) " +
            $"and holds {r.FileCopy.Deliveries} of 5 deliveries (the others were still in contoso.db-wal)",
            r.FileCopy.Deliveries < 5 && r.Manifest.Counts["deliveries"] == 5);
        Expect($"online backup: {r.Manifest.Counts["deliveries"]} deliveries, {r.Manifest.Counts["approvals"]} approvals, " +
            $"{r.Manifest.Counts["workflow_state"]} workflow run, schema {r.Manifest.Schema}",
            r.Manifest.Counts["deliveries"] == 5 && r.Manifest.Counts["approvals"] == 5 && r.Manifest.Counts["workflow_state"] == 1);
        Expect("rollback binary 2.4.0 refused before any row is read (schema 3 > 2)",
            !r.Attempts[0].Result.Ok && r.Attempts[0].Result.Detail.Contains("schema 3", StringComparison.Ordinal));
        Expect("binary 3.0.0 refused: the index was built with another embedding model",
            !r.Attempts[1].Result.Ok && r.Attempts[1].Result.Detail.Contains("nomic-embed-text", StringComparison.Ordinal));
        Expect("restore over the live path refused", !r.Attempts[2].Result.Ok && r.Attempts[2].Result.Detail.Contains("live store"));
        Expect($"DEFECT REPRODUCED: read by the 2.x code, op5's approval says {Drill.Money(r.Op5Approval.V2)} EUR " +
            $"(schema 3 stores {Drill.Money(r.Op5Approval.V3)} EUR as minor units); the {Drill.Money(r.Op5Approval.Claimed)} EUR claim would pass",
            r.Op5Approval.V2 == 5000m && r.Op5Approval.V3 == 50m && r.Op5Approval.Claimed <= r.Op5Approval.V2);
        Expect($"the restore read its manifest back from {r.Manifest.File}.manifest.json: sha256, newest write, identity and counts as written",
            r.Restored.Ok && r.Manifest.Sha256 == r.Written.Sha256 && r.Manifest.NewestWrite == r.Written.NewestWrite &&
            r.Manifest.IdentityDigest == r.Written.IdentityDigest && r.Manifest.Counts.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .SequenceEqual(r.Written.Counts.OrderBy(kv => kv.Key, StringComparer.Ordinal)));
        Expect("3.1.0 holds op5: 450.00 claimed, 50.00 approved",
            r.Reconcile.Fates.Any(f => Drill.Label(f.Reference) == "op5" && f.State == "Held"));

        Dictionary<string, DeliveryStatus> final = r.Final.ToDictionary(d => Drill.Label(d.Reference), d => d.Status);
        Expect($"every operation has a stated end: {string.Join(", ", final.Select(kv => $"{kv.Key} {kv.Value}"))}",
            final.Count == 7 && final["op3"] == DeliveryStatus.Reconciled && final["op4"] == DeliveryStatus.Delivered &&
            final["op5"] == DeliveryStatus.Conflict && final["op6"] == DeliveryStatus.Reconciled && final["op7"] == DeliveryStatus.Reconciled);
        Expect($"written after the backup ({string.Join(", ", r.WrittenAfterBackup)}) = paid at the provider, missing from the restore " +
            $"({string.Join(", ", r.Reconcile.Uncertain.Select(u => Drill.Label(u.Reference)))}): both on the uncertainty list",
            r.Reconcile.Uncertain.Select(u => Drill.Label(u.Reference)).SequenceEqual(r.WrittenAfterBackup));
        Expect($"payments at the provider: {r.EffectsBeforeIncident} before the incident, {r.EffectsAfter} after (op4 sent once, nothing paid twice)",
            r.EffectsBeforeIncident == 5 && r.EffectsAfter == 6);
        {
            DeliveryWorker dispatcher = new(r.LivePath, new FakePaymentProvider(r.Journal));
            DeliveryStatus op5 = await dispatcher.DeliverAsync(
                new Delivery("contoso", Drill.Op(5), "E-1001", Drill.Money(450m), "EUR"), CancellationToken.None);
            int resumed = (await dispatcher.ResumeAsync(CancellationToken.None)).Count;
            long payments = new FakePaymentProvider(r.Journal).EffectCount();
            Expect($"the hold is in the promoted store: dispatching op5 again -> {op5}, resume picks up {resumed} rows, provider payments {payments}",
                op5 == DeliveryStatus.Conflict && resumed == 0 && payments == 6);
        }
        Expect($"RTO measured {r.Rto.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture)} s <= lab target {DrillReport.RtoTarget.TotalSeconds:0} s; " +
            $"RPO {r.Rpo.TotalMinutes:0} min <= {DrillReport.RpoTarget.TotalMinutes:0} min",
            r.Rto > TimeSpan.Zero && r.Rto <= DrillReport.RtoTarget && r.Rpo == TimeSpan.FromMinutes(20) && r.Rpo <= DrillReport.RpoTarget);

        DrillReport two = await Drill.RunAsync(secondRun, _ => { }, resumers: 2);
        Dictionary<string, DeliveryStatus> twoFinal = two.Final.ToDictionary(d => Drill.Label(d.Reference), d => d.Status);
        Expect($"two reconcilers at once (Task.Run + Barrier): {two.EffectsAfter} payments, op4 {twoFinal["op4"]} " +
            "(nothing paid twice)",
            two.EffectsAfter == 6 && twoFinal["op4"] is DeliveryStatus.Delivered or DeliveryStatus.Reconciled);

        Console.WriteLine($"\nRESULT {s_passed}/{s_total}");
        return s_passed == s_total ? 0 : 1;
    }

    static void Expect(string name, bool ok)
    {
        s_total++;
        if (ok) s_passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    }
}
