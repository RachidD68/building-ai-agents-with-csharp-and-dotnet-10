using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Ch08.UncertainOutcome;

public sealed record PaymentRequest(string Reference, string EmployeeId, string Amount, string Currency);

/// <summary>What the provider holds under a reference, so the caller can check it is its own payment.</summary>
public sealed record PaymentReceipt(string PaymentId, string Reference, string EmployeeId, string Amount, string Currency)
{
    public bool Matches(PaymentRequest r) =>
        Reference == r.Reference && EmployeeId == r.EmployeeId && Amount == r.Amount && Currency == r.Currency;
}

/// <summary>The reference is already held by a payment with another payee, amount or currency.</summary>
public sealed class ReferenceConflictException(string reference)
    : InvalidOperationException($"reference '{reference}' already belongs to a different payment");

/// <summary>The provider's side of the boundary, as the worker sees it.</summary>
public interface IPaymentProvider
{
    /// <summary>
    /// Pays once per reference: a second request with the same reference
    /// returns the first receipt and moves no money (the provider's
    /// idempotency key). A repeated reference whose payee, amount or currency
    /// differ is refused with <see cref="ReferenceConflictException"/>. May
    /// throw <see cref="TimeoutException"/>, in which case the caller cannot
    /// tell whether the payment happened.
    /// </summary>
    Task<PaymentReceipt> PayAsync(PaymentRequest request, CancellationToken ct);

    /// <summary>Looks a payment up by the caller's stable reference.</summary>
    Task<PaymentReceipt?> FindByReferenceAsync(string reference, CancellationToken ct);
}

public enum ProviderFault
{
    None,
    /// <summary>The provider records the payment, then the response is lost.</summary>
    AcceptThenTimeout,
    /// <summary>The request never reaches the provider; the caller sees a timeout.</summary>
    LostRequest,
    /// <summary>The provider records the payment, then the calling process dies.</summary>
    CrashAfterAccept,
}

/// <summary>
/// A simulated payment provider. It keeps its own journal (a separate SQLite
/// file, standing in for the provider's database), so a second process sees
/// what the first one paid. Every row in the journal is one simulated
/// external effect. The fault applies to the first request only.
/// </summary>
public sealed class FakePaymentProvider(string journalPath, ProviderFault fault = ProviderFault.None)
    : IPaymentProvider
{
    public const int CrashExitCode = 3;
    private ProviderFault _fault = fault;

    public Task<PaymentReceipt> PayAsync(PaymentRequest request, CancellationToken ct)
    {
        ProviderFault fault = _fault;
        _fault = ProviderFault.None;
        if (fault == ProviderFault.LostRequest)
            throw new TimeoutException("the request timed out before the provider received it");

        using SqliteConnection db = Open();
        using (SqliteCommand insert = db.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO payments (reference, payment_id, employee_id, amount, currency, accepted_at)
                VALUES ($reference, $paymentId, $employee, $amount, $currency, $now)
                ON CONFLICT (reference) DO NOTHING
                """;
            insert.Parameters.AddWithValue("$reference", request.Reference);
            insert.Parameters.AddWithValue("$paymentId", "PAY-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Reference)))[..12]);
            insert.Parameters.AddWithValue("$employee", request.EmployeeId);
            insert.Parameters.AddWithValue("$amount", request.Amount);
            insert.Parameters.AddWithValue("$currency", request.Currency);
            insert.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            insert.ExecuteNonQuery();
        }
        // The key is only safe if the provider compares the arguments: the same
        // reference with another payee or amount is a different payment, not a retry.
        PaymentReceipt receipt = Find(db, request.Reference)!;
        if (!receipt.Matches(request)) throw new ReferenceConflictException(request.Reference);

        switch (fault)
        {
            case ProviderFault.AcceptThenTimeout:
                throw new TimeoutException("the provider accepted the payment, then the response was lost");
            case ProviderFault.CrashAfterAccept:
                Console.WriteLine("  [fault] the provider accepted the payment; the worker process dies now");
                Console.Out.Flush();
                Environment.Exit(CrashExitCode);
                break;
        }
        return Task.FromResult(receipt);
    }

    public Task<PaymentReceipt?> FindByReferenceAsync(string reference, CancellationToken ct)
    {
        using SqliteConnection db = Open();
        return Task.FromResult(Find(db, reference));
    }

    /// <summary>Number of simulated external effects (payments) in the journal.</summary>
    public long EffectCount()
    {
        using SqliteConnection db = Open();
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM payments";
        return (long)cmd.ExecuteScalar()!;
    }

    private static PaymentReceipt? Find(SqliteConnection db, string reference)
    {
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = "SELECT payment_id, employee_id, amount, currency FROM payments WHERE reference = $reference";
        cmd.Parameters.AddWithValue("$reference", reference);
        using SqliteDataReader r = cmd.ExecuteReader();
        return r.Read() ? new PaymentReceipt(r.GetString(0), reference, r.GetString(1), r.GetString(2), r.GetString(3)) : null;
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = journalPath,
            Pooling = false,
        }.ToString());
        db.Open();
        using SqliteCommand create = db.CreateCommand();
        create.CommandText = """
            CREATE TABLE IF NOT EXISTS payments (
                reference   TEXT PRIMARY KEY,      -- the caller's idempotency key
                payment_id  TEXT NOT NULL,
                employee_id TEXT NOT NULL,
                amount      TEXT NOT NULL,
                currency    TEXT NOT NULL,
                accepted_at TEXT NOT NULL)
            """;
        create.ExecuteNonQuery();
        return db;
    }
}
