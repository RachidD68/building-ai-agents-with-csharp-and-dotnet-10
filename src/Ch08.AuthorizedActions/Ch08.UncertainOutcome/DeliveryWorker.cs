using Microsoft.Data.Sqlite;

namespace Ch08.UncertainOutcome;

public enum DeliveryStatus { Pending, Sending, Delivered, OutcomeUnknown, Reconciled, Conflict }

/// <summary>One external effect to deliver, keyed by the business operation.</summary>
public sealed record Delivery(
    string Tenant, Guid OperationId, string EmployeeId, string Amount, string Currency,
    DeliveryStatus Status = DeliveryStatus.Pending, string? PaymentId = null, int Attempts = 0)
{
    /// <summary>
    /// The provider reference: stable across retries, and unique across
    /// everything the provider sees. Operation ids are unique only inside a
    /// tenant here, so the tenant is part of the key.
    /// </summary>
    public string Reference => $"{Tenant}:{OperationId:D}";
}

public sealed record ReconcileResult(Guid OperationId, DeliveryStatus Status, string How);

/// <summary>
/// Delivers effects to the provider and keeps the delivery state in the
/// application's SQLite file. The state is written BEFORE the call, so a
/// process that dies during the call leaves a row the next run must reconcile.
/// </summary>
public sealed class DeliveryWorker(string databasePath, IPaymentProvider provider)
{
    public const string Schema = """
        CREATE TABLE IF NOT EXISTS deliveries (
            tenant       TEXT    NOT NULL,
            operation_id TEXT    NOT NULL,
            employee_id  TEXT    NOT NULL,
            amount       TEXT    NOT NULL,
            currency     TEXT    NOT NULL,
            status       TEXT    NOT NULL,
            payment_id   TEXT,
            attempts     INTEGER NOT NULL DEFAULT 0,
            updated_at   TEXT    NOT NULL,
            PRIMARY KEY (tenant, operation_id))
        """;

    public async Task<DeliveryStatus> DeliverAsync(Delivery delivery, CancellationToken ct)
    {
        Enqueue(delivery);
        // Pending -> Sending in one conditional UPDATE, recorded BEFORE the call.
        // A second worker (or a repeated dispatch) loses here and never resends.
        if (!TryClaimForSending(delivery))
            return Load(delivery.Tenant, delivery.OperationId)!.Status;

        Delivery current = Load(delivery.Tenant, delivery.OperationId)!;
        try
        {
            PaymentReceipt receipt = await provider.PayAsync(Request(current), ct);
            return Settle(current, receipt, DeliveryStatus.Delivered);
        }
        catch (TimeoutException)
        {
            // The provider may or may not have paid. Record exactly that.
            Save(current with { Status = DeliveryStatus.OutcomeUnknown });
            return DeliveryStatus.OutcomeUnknown;
        }
        catch (ReferenceConflictException)
        {
            Save(current with { Status = DeliveryStatus.Conflict });
            return DeliveryStatus.Conflict;
        }
    }

    /// <summary>
    /// Records a receipt only if it is this delivery's payment: same reference,
    /// payee, amount and currency. Anything else is a Conflict for a person to
    /// look at, never "paid".
    /// </summary>
    private DeliveryStatus Settle(Delivery d, PaymentReceipt receipt, DeliveryStatus status, int attempts = 0)
    {
        if (!receipt.Matches(Request(d))) status = DeliveryStatus.Conflict;
        Save(d with
        {
            Status = status,
            PaymentId = status == DeliveryStatus.Conflict ? null : receipt.PaymentId,
            Attempts = d.Attempts + attempts,
        });
        return status;
    }

    private bool TryClaimForSending(Delivery d)
    {
        using SqliteConnection db = Open();
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE deliveries SET status = 'Sending', attempts = attempts + 1, updated_at = $now
            WHERE tenant = $tenant AND operation_id = $op AND status = 'Pending'
            """;
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$tenant", d.Tenant);
        cmd.Parameters.AddWithValue("$op", d.OperationId.ToString("D"));
        return cmd.ExecuteNonQuery() == 1;
    }

    /// <summary>
    /// Resolves every delivery whose outcome is not known: ask the provider by
    /// the stable reference first; send again only when it has no record, with
    /// the SAME reference, so the provider's idempotency key absorbs a request
    /// that was still in flight. A receipt for another payment is a Conflict.
    /// Each row runs on its own: any other error (a refused payee, a provider 5xx,
    /// a database fault) is logged and leaves that row as it was for the next
    /// resume, and the other rows still settle.
    /// </summary>
    public async Task<IReadOnlyList<ReconcileResult>> ResumeAsync(CancellationToken ct)
    {
        List<ReconcileResult> results = [];
        foreach (Delivery d in LoadUnresolved())       // status Sending or OutcomeUnknown
        {
            try
            {
                results.Add(await ReconcileAsync(d, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                Console.Error.WriteLine($"reconcile of {d.Reference} failed, left for the next resume: " +
                                        $"{ex.GetType().Name}: {ex.Message}");
                results.Add(new(d.OperationId, d.Status, "failed; left for the next resume"));
            }
        }
        return results;
    }

    private async Task<ReconcileResult> ReconcileAsync(Delivery d, CancellationToken ct)
    {
        PaymentReceipt? found = await provider.FindByReferenceAsync(d.Reference, ct);
        if (found is not null)
        {
            DeliveryStatus status = Settle(d, found, DeliveryStatus.Reconciled);
            return new(d.OperationId, status, status == DeliveryStatus.Reconciled
                ? $"found {found.PaymentId} by reference" : "the reference holds a different payment");
        }
        try
        {
            PaymentReceipt receipt = await provider.PayAsync(Request(d), ct);
            DeliveryStatus status = Settle(d, receipt, DeliveryStatus.Delivered, attempts: 1);
            return new(d.OperationId, status, status == DeliveryStatus.Delivered
                ? $"not found; sent again as {receipt.PaymentId}" : "the reference holds a different payment");
        }
        catch (TimeoutException)
        {
            Save(d with { Status = DeliveryStatus.OutcomeUnknown, Attempts = d.Attempts + 1 });
            return new(d.OperationId, DeliveryStatus.OutcomeUnknown, "still unknown; try the next resume");
        }
        catch (ReferenceConflictException)
        {
            Save(d with { Status = DeliveryStatus.Conflict, Attempts = d.Attempts + 1 });
            return new(d.OperationId, DeliveryStatus.Conflict, "the reference holds a different payment");
        }
    }

    public Delivery? Load(string tenant, Guid operationId) =>
        Query("WHERE tenant = $tenant AND operation_id = $op",
            ("$tenant", tenant), ("$op", operationId.ToString("D"))).SingleOrDefault();

    public IReadOnlyList<Delivery> LoadUnresolved() =>
        Query("WHERE status IN ('Sending', 'OutcomeUnknown') ORDER BY updated_at");

    private static PaymentRequest Request(Delivery d) => new(d.Reference, d.EmployeeId, d.Amount, d.Currency);

    private void Enqueue(Delivery d)
    {
        using SqliteConnection db = Open();
        Execute(db, """
            INSERT INTO deliveries (tenant, operation_id, employee_id, amount, currency, status, updated_at)
            VALUES ($tenant, $op, $employee, $amount, $currency, 'Pending', $now)
            ON CONFLICT (tenant, operation_id) DO NOTHING
            """, ("$tenant", d.Tenant), ("$op", d.OperationId.ToString("D")), ("$employee", d.EmployeeId),
            ("$amount", d.Amount), ("$currency", d.Currency), ("$now", DateTimeOffset.UtcNow.ToString("O")));
    }

    private void Save(Delivery d)
    {
        using SqliteConnection db = Open();
        Execute(db, """
            UPDATE deliveries SET status = $status, payment_id = $paymentId, attempts = $attempts, updated_at = $now
            WHERE tenant = $tenant AND operation_id = $op
            """, ("$status", d.Status.ToString()), ("$paymentId", (object?)d.PaymentId ?? DBNull.Value),
            ("$attempts", d.Attempts), ("$now", DateTimeOffset.UtcNow.ToString("O")),
            ("$tenant", d.Tenant), ("$op", d.OperationId.ToString("D")));
    }

    private List<Delivery> Query(string where, params (string, object)[] parameters)
    {
        using SqliteConnection db = Open();
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = "SELECT tenant, operation_id, employee_id, amount, currency, status, payment_id, attempts " +
                          "FROM deliveries " + where;
        foreach ((string name, object value) in parameters) cmd.Parameters.AddWithValue(name, value);
        using SqliteDataReader r = cmd.ExecuteReader();
        List<Delivery> rows = [];
        while (r.Read())
            rows.Add(new Delivery(r.GetString(0), Guid.Parse(r.GetString(1)), r.GetString(2), r.GetString(3),
                r.GetString(4), Enum.Parse<DeliveryStatus>(r.GetString(5)),
                r.IsDBNull(6) ? null : r.GetString(6), r.GetInt32(7)));
        return rows;
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString());
        db.Open();
        Execute(db, Schema);
        return db;
    }

    private static void Execute(SqliteConnection db, string sql, params (string, object)[] parameters)
    {
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach ((string name, object value) in parameters) cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }
}
