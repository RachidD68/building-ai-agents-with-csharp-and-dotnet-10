namespace Ch22.CostModel;

/// <summary>
/// Reserve before the call, reconcile after it. A task reserves its worst
/// case (the max output it may produce, priced); the reservation holds that
/// amount against the limit while the call runs, so concurrent tasks cannot
/// all see the same free balance. Reconciliation books the measured cost and
/// releases the rest. This is a policy counter: it enforces a ceiling, it does
/// not predict the provider's invoice.
/// </summary>
public sealed class BudgetLedger(decimal limit)
{
    readonly Lock _gate = new();
    readonly Dictionary<Guid, decimal> _open = [];
    decimal _spent, _reserved;

    public decimal Limit => limit;
    public decimal Spent { get { lock (_gate) return _spent; } }
    public decimal Reserved { get { lock (_gate) return _reserved; } }
    public decimal Overrun { get; private set; }

    public Guid? TryReserve(decimal worstCase)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(worstCase);
        lock (_gate)
        {
            if (_spent + _reserved + worstCase > limit) return null;   // refuse before the call
            Guid id = Guid.NewGuid();
            _open[id] = worstCase;
            _reserved += worstCase;
            return id;
        }
    }

    /// <summary>Books the measured cost. A cost above the reservation is booked
    /// too (the money is spent) and recorded as an overrun of the estimate.</summary>
    public void Reconcile(Guid reservation, decimal actual)
    {
        lock (_gate)
        {
            if (!_open.Remove(reservation, out decimal held))
                throw new InvalidOperationException("Unknown or already reconciled reservation.");
            _reserved -= held;
            _spent += actual;
            if (actual > held) Overrun += actual - held;
        }
    }
}

/// <summary>The first-edition shape: check the balance, call, then add. Kept
/// to reproduce the defect: every concurrent caller passes the same check.</summary>
public sealed class CheckThenSpendBudget(decimal limit)
{
    readonly Lock _gate = new();
    decimal _spent;
    public decimal Limit => limit;
    public decimal Spent { get { lock (_gate) return _spent; } }
    public bool CanAfford(decimal cost) => Spent + cost <= limit;
    public void Spend(decimal cost) { lock (_gate) _spent += cost; }
}
