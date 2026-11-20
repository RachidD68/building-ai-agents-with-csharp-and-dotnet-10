using System.Diagnostics;
using System.Globalization;
using Ch08.UncertainOutcome;
using Microsoft.Data.Sqlite;

namespace Ch24.RestoreDrill;

public sealed class DrillClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void At(int hour, int minute) => _now = new DateTimeOffset(_now.Date, TimeSpan.Zero).AddHours(hour).AddMinutes(minute);
}

public sealed record DrillReport(
    (long Bytes, long Tables, long Deliveries) FileCopy, BackupManifest Written, BackupManifest Manifest,
    IReadOnlyList<(string Attempt, RestoreResult Result)> Attempts, RestoreResult Restored,
    (decimal V2, decimal V3, decimal Claimed) Op5Approval, ReconcileReport Reconcile,
    long EffectsBeforeIncident, long EffectsAfter, IReadOnlyList<Delivery> Final,
    IReadOnlyList<string> WrittenAfterBackup, DateTimeOffset Incident, TimeSpan Rto, TimeSpan Rpo,
    string LivePath, string Journal)
{
    public static readonly TimeSpan RtoTarget = TimeSpan.FromSeconds(60);   // lab choice
    public static readonly TimeSpan RpoTarget = TimeSpan.FromMinutes(30);   // lab choice
}

/// <summary>
/// One restore drill on temporary storage. The live store takes payments
/// through Ch08's DeliveryWorker and the simulated provider (whose journal is
/// the provider's, outside the incident), is backed up at 09:20, keeps
/// working, and is lost at 09:35. The clock starts when the incident is
/// declared and stops when the restored store is reconciled and promoted.
/// </summary>
public static class Drill
{
    public static readonly Release Current = new("3.1.0", 3, 3, "nomic-embed-text");
    public static readonly Release Rollback = new("2.4.0", 1, 2, "nomic-embed-text");
    public static readonly Release OtherIndex = new("3.0.0", 3, 3, "text-embedding-3-small");
    public static readonly ConfigurationIdentity Identity = new("llama3.2",
        PromptSha256: "5f1c0e9a22b47d10", SkillSetSha256: "a93d6b2e0c4f7781", IndexVersion: "faq-2026-11-18",
        EmbeddingModel: "nomic-embed-text");   // lab labels, not measurements

    public static Guid Op(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:D12}");
    public static string Label(string reference) => "op" + int.Parse(reference[^12..], CultureInfo.InvariantCulture);

    public static async Task<DrillReport> RunAsync(string work, Action<string> log, int resumers = 1)
    {
        DrillClock clock = new(new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero));
        ContosoStore live = new(Path.Combine(work, "live", "contoso.db"), clock);
        Directory.CreateDirectory(Path.GetDirectoryName(live.Path)!);
        string journal = Path.Combine(work, "provider", "journal.db");
        Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
        string backups = Path.Combine(work, "backups");
        live.Create(Identity);
        // The running service. Once it has read, its connection holds the WAL open, so
        // nothing is checkpointed into the main file when other connections close.
        SqliteConnection service = live.Open();
        StoreFacts.Counts(service);
        List<string> afterBackup = [];
        bool backedUp = false;

        async Task Pay(int n, int h, int m, string employee, decimal amount, ProviderFault fault)
        {
            clock.At(h, m);
            live.Conversation($"C-{n}", "contoso", "alice");
            live.Approve("contoso", Op(n), "maria", amount);
            DeliveryStatus s = await new DeliveryWorker(live.Path, new FakePaymentProvider(journal, fault))
                .DeliverAsync(new Delivery("contoso", Op(n), employee, Money(amount), "EUR"), CancellationToken.None);
            if (backedUp) afterBackup.Add($"op{n}");
            log($"  {h:D2}:{m:D2}  op{n} {employee} {Money(amount)} EUR  provider fault {fault} -> {s}");
        }

        await Pay(1, 9, 0, "E-1001", 30m, ProviderFault.None);
        await Pay(2, 9, 5, "E-1002", 45m, ProviderFault.None);
        await Pay(3, 9, 10, "E-1001", 25m, ProviderFault.AcceptThenTimeout);
        await Pay(4, 9, 12, "E-1002", 15m, ProviderFault.LostRequest);
        clock.At(9, 15);
        live.Conversation("C-5", "contoso", "alice");
        live.Approve("contoso", Op(5), "maria", 50m);
        live.HoldDelivery(new Delivery("contoso", Op(5), "E-1001", Money(450m), "EUR"));
        live.Checkpoint("W-5", "awaiting_second_approval", """{"operation":"op5"}""");
        log("  09:15  op5 E-1001 450.00 EUR, approved 50.00 -> Pending; workflow W-5 checkpointed");

        clock.At(9, 18);
        string copied = Backups.FileCopy(live, backups);
        (long, long, long) fileCopy;
        using (SqliteConnection c = new($"Data Source={copied};Mode=ReadOnly;Pooling=False"))
        {
            c.Open();
            SortedDictionary<string, long> counts = StoreFacts.Counts(c);
            fileCopy = (new FileInfo(copied).Length, counts.Count(kv => kv.Value >= 0), counts["deliveries"]);
        }
        log($"  09:18  File.Copy of contoso.db returned: {fileCopy.Item1} bytes");

        clock.At(9, 20);
        BackupManifest written = Backups.Online(live, backups, clock.GetUtcNow()), manifest = written;
        backedUp = true;
        log($"  09:20  online backup {manifest.File}: newest write {manifest.NewestWrite:HH:mm}, schema {manifest.Schema}, sha256 {manifest.Sha256[..12]}");

        await Pay(6, 9, 25, "E-1002", 20m, ProviderFault.None);
        await Pay(7, 9, 30, "E-1001", 35m, ProviderFault.AcceptThenTimeout);
        long effectsBefore = new FakePaymentProvider(journal).EffectCount();

        clock.At(9, 35);
        DateTimeOffset incident = clock.GetUtcNow();
        service.Dispose();
        foreach (string suffix in new[] { "", "-wal", "-shm" }) File.Delete(live.Path + suffix);
        log($"  09:35  INCIDENT: live store lost. Provider journal holds {effectsBefore} payments.");
        Stopwatch rto = Stopwatch.StartNew();
        manifest = Backups.ReadManifest(backups);                // what survives the incident is the file
        List<(string, RestoreResult)> attempts =
        [
            ($"binary {Rollback}", Restores.Into(backups, manifest, Rollback, Path.Combine(work, "restore-a"), live.Path)),
            ($"binary {OtherIndex}", Restores.Into(backups, manifest, OtherIndex, Path.Combine(work, "restore-b"), live.Path)),
            ($"binary {Current} onto the live path", Restores.Into(backups, manifest, Current, Path.GetDirectoryName(live.Path)!, live.Path)),
        ];
        RestoreResult restored = Restores.Into(backups, manifest, Current, Path.Combine(work, "restore-c"), live.Path);
        if (!restored.Ok) throw new InvalidOperationException(restored.Detail);

        (decimal, decimal, decimal) op5;
        using (SqliteConnection db = new($"Data Source={restored.Path};Mode=ReadOnly;Pooling=False"))
        {
            db.Open();
            Delivery d = StoreFacts.Deliveries(db).Single(x => x.OperationId == Op(5));
            op5 = (Reconciler.ReadApprovalV2(db, d)!.Value, Reconciler.ReadApprovalV3(db, d)!.Value, decimal.Parse(d.Amount, CultureInfo.InvariantCulture));
        }

        ReconcileReport report = await Reconciler.RunAsync(restored.Path!, journal, resumers);
        File.Copy(restored.Path!, live.Path);                    // promote the verified, reconciled copy
        rto.Stop();

        List<Delivery> final;
        using (SqliteConnection db = new($"Data Source={live.Path};Mode=ReadOnly;Pooling=False"))
        {
            db.Open();
            final = StoreFacts.Deliveries(db);
        }
        return new(fileCopy, written, manifest, attempts, restored, op5, report, effectsBefore,
            new FakePaymentProvider(journal).EffectCount(), final, afterBackup, incident, rto.Elapsed, incident - manifest.NewestWrite,
            live.Path, journal);
    }

    public static string Money(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);
}
