using System.Globalization;
using Ch08.UncertainOutcome;
using Microsoft.Data.Sqlite;

namespace Ch24.RestoreDrill;

/// <summary>Where one operation ended after the restore, and how that was decided.</summary>
public sealed record Fate(string Reference, string State, string How);

public sealed record ReconcileReport(IReadOnlyList<Fate> Fates, IReadOnlyList<Fate> Uncertain);

/// <summary>
/// The provider's settlement statement: every payment it holds, by reference.
/// Read from the simulated provider's own journal, which the incident did not
/// touch. A restore brings the application back to the recovery point; the
/// provider's money moved anyway, and restoring code or data never undoes that.
/// </summary>
public static class ProviderStatement
{
    public static Dictionary<string, PaymentReceipt> Read(string journalPath)
    {
        using SqliteConnection db = new($"Data Source={journalPath};Mode=ReadOnly;Pooling=False");
        db.Open();
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = "SELECT reference, payment_id, employee_id, amount, currency FROM payments";
        using SqliteDataReader r = cmd.ExecuteReader();
        Dictionary<string, PaymentReceipt> rows = new(StringComparer.Ordinal);
        while (r.Read()) rows[r.GetString(0)] = new(r.GetString(1), r.GetString(0), r.GetString(2), r.GetString(3), r.GetString(4));
        return rows;
    }
}

public static class Reconciler
{
    /// <summary>
    /// Brings every operation the restored store knows, and every payment the
    /// provider knows, to a stated end:
    ///  - Pending: held when its approval does not cover the amount, otherwise
    ///    left for the normal dispatcher. The hold is written to the store as
    ///    Conflict, the delivery worker's state for "a person decides": no
    ///    worker claims it (DeliverAsync claims Pending, ResumeAsync resumes
    ///    Sending and OutcomeUnknown);
    ///  - Sending / OutcomeUnknown: Ch08's DeliveryWorker asks the provider by
    ///    reference and sends again only when the provider has no record;
    ///  - a payment in the statement with no row in the store (made after the
    ///    recovery point): recorded from the statement, and listed as uncertain,
    ///    because its approval and conversation records are gone.
    /// <paramref name="resumers"/> &gt; 1 runs that many DeliveryWorkers at once.
    /// </summary>
    public static async Task<ReconcileReport> RunAsync(string restoredDb, string journalPath, int resumers = 1)
    {
        List<Fate> fates = [], uncertain = [];
        List<Delivery> rows;
        using (SqliteConnection db = new($"Data Source={restoredDb};Pooling=False"))
        {
            db.Open();
            rows = StoreFacts.Deliveries(db);
            foreach (Delivery d in rows.Where(r => r.Status is DeliveryStatus.Pending or DeliveryStatus.Sending or DeliveryStatus.OutcomeUnknown))
            {
                decimal? approved = ReadApprovalV3(db, d);
                decimal amount = decimal.Parse(d.Amount, CultureInfo.InvariantCulture);
                if (approved is null || amount > approved)
                {
                    StoreSchema.Exec(db, null, """
                        UPDATE deliveries SET status = 'Conflict', updated_at = $now
                        WHERE tenant = $t AND operation_id = $op AND status = $was
                        """, ("$t", d.Tenant), ("$op", d.OperationId.ToString("D")), ("$was", d.Status.ToString()),
                        ("$now", DateTimeOffset.UtcNow.ToString("O")));
                    fates.Add(new(d.Reference, "Held", $"{d.Amount} {d.Currency} claimed, {approved?.ToString("0.00", CultureInfo.InvariantCulture) ?? "no"} approved; stored as Conflict until a new approval"));
                    if (d.Status != DeliveryStatus.Pending)
                        uncertain.Add(new(d.Reference, d.Status.ToString(), "sent once, now over its approval: a person decides"));
                }
                else if (d.Status == DeliveryStatus.Pending)
                {
                    fates.Add(new(d.Reference, "Pending", "approval covers it; left for the normal dispatcher"));
                }
            }
        }
        if (uncertain.Count > 0) return new(fates, uncertain);   // never resume past a row nobody can vouch for

        IPaymentProvider provider = new FakePaymentProvider(journalPath);
        using Barrier start = new(resumers);
        IReadOnlyList<ReconcileResult>[] runs = await Task.WhenAll(Enumerable.Range(0, resumers).Select(_ => Task.Run(async () =>
        {
            start.SignalAndWait();
            return await new DeliveryWorker(restoredDb, provider).ResumeAsync(CancellationToken.None);
        })));
        foreach (ReconcileResult r in runs[0])
            fates.Add(new($"contoso:{r.OperationId:D}", r.Status.ToString(), r.How));

        using SqliteConnection after = new($"Data Source={restoredDb};Pooling=False");
        after.Open();
        HashSet<string> known = [.. StoreFacts.Deliveries(after).Select(d => d.Reference)];
        foreach (PaymentReceipt p in ProviderStatement.Read(journalPath).Values.Where(p => !known.Contains(p.Reference)).OrderBy(p => p.Reference))
        {
            string[] key = p.Reference.Split(':', 2);
            StoreSchema.Exec(after, null, """
                INSERT INTO deliveries (tenant, operation_id, employee_id, amount, currency, status, payment_id, attempts, updated_at)
                VALUES ($t, $op, $e, $a, $c, 'Reconciled', $pay, 0, $now)
                """, ("$t", key[0]), ("$op", key[1]), ("$e", p.EmployeeId), ("$a", p.Amount), ("$c", p.Currency),
                ("$pay", p.PaymentId), ("$now", DateTimeOffset.UtcNow.ToString("O")));
            fates.Add(new(p.Reference, "Reconciled", $"from the provider statement ({p.PaymentId}, {p.Amount} {p.Currency})"));
            uncertain.Add(new(p.Reference, "Reconciled", "paid after the recovery point; approval and conversation records lost"));
        }
        return new(fates, uncertain);
    }

    /// <summary>How the 3.x binary reads an approval: minor units.</summary>
    public static decimal? ReadApprovalV3(SqliteConnection db, Delivery d) =>
        StoreFacts.ApprovalAmountText(db, d.Tenant, d.OperationId) is { } text
            ? long.Parse(text, CultureInfo.InvariantCulture) / 100m : null;

    /// <summary>How the 2.x binary reads the same column: major units. Wrong on schema 3.</summary>
    public static decimal? ReadApprovalV2(SqliteConnection db, Delivery d) =>
        StoreFacts.ApprovalAmountText(db, d.Tenant, d.OperationId) is { } text
            ? decimal.Parse(text, CultureInfo.InvariantCulture) : null;
}
