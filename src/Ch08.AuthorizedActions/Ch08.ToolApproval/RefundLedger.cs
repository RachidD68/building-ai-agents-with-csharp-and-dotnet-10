using System.Globalization;

namespace Ch08.ToolApproval;

/// <summary>
/// One credit per order, however many times the approved call runs. Approval
/// authorizes the refund; it does not stop a retry after a timeout, a resumed
/// session or a double-clicked button from running it again. In a real system
/// this is a UNIQUE key in the payments database (see Ch08.IdempotentCommit)
/// plus the provider's own idempotency key, not a dictionary.
/// </summary>
internal sealed class RefundLedger
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _confirmations = new(StringComparer.OrdinalIgnoreCase);
    private int _creditsIssued;

    public int CreditsIssued => Volatile.Read(ref _creditsIssued);

    public string Refund(string orderId, decimal amount, string currency)
    {
        string key = orderId.Trim();
        // The check and the credit happen under one lock, so two concurrent
        // calls for the same order can never both issue a credit.
        lock (_gate)
        {
            if (_confirmations.TryGetValue(key, out string? earlier))
                return $"{earlier} (Already refunded earlier; no second credit was issued.)";

            // A real system would call the payments service here.
            string confirmation = $"Refund processed for {key.ToUpperInvariant()}: a credit of " +
                                  $"{amount.ToString("0.00", CultureInfo.InvariantCulture)} {currency} was issued.";
            _creditsIssued++;
            _confirmations[key] = confirmation;
            return confirmation;
        }
    }

    /// <summary>The ledger's 6 checks, on a fresh ledger.</summary>
    public static (int Passed, int Total) SelfTest()
    {
        var ledger = new RefundLedger();
        int passed = 0, total = 0;
        void Check(string name, bool ok)
        {
            total++; passed += ok ? 1 : 0;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
        }

        string first = ledger.Refund("ORD-1001", 20m, "USD");
        string again = ledger.Refund("ORD-1001", 20m, "USD");
        string sameIdOtherCase = ledger.Refund(" ord-1001 ", 20m, "USD");
        Check("first refund issues a credit", first.StartsWith("Refund processed", StringComparison.Ordinal) && !first.Contains("Already"));
        Check("second refund of the same order issues no credit", again.Contains("no second credit"));
        Check("order id is matched case-insensitively and trimmed", sameIdOtherCase.Contains("no second credit"));
        Check("exactly one credit for ORD-1001", ledger.CreditsIssued == 1);

        Parallel.For(0, 50, _ => ledger.Refund("ORD-2002", 80m, "USD"));
        Check("50 concurrent refunds of ORD-2002 issue one credit", ledger.CreditsIssued == 2);

        ledger.Refund("ORD-3003", 10m, "USD");
        Check("a different order gets its own credit", ledger.CreditsIssued == 3);
        return (passed, total);
    }
}
