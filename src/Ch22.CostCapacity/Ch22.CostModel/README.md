# Ch22.CostModel

Cost per **accepted task**, not per model call, from the `UsageDetails` each call reported and a price table supplied as data.

- `PriceTable`: currency, effective date, unit (`per-million-tokens`), one rate per model, tool and runtime host. A table without a currency, a date or the unit is refused; usage dated before the table takes effect is refused; an unknown model is refused, never priced at zero.
- `CostCalculator`: cached input is part of `InputTokenCount` and reasoning is part of `OutputTokenCount` (the `UsageDetails` contract), so each is split out once. A call with no usage makes the total a lower bound (`Complete = false`). Runtime hours are priced too: a local model with zero token rates still costs its machine.
- `CostReport.CostPerAcceptedTask` is `null` (printed `undefined`) when nothing was accepted.
- `BudgetLedger`: reserve the worst case before the call, reconcile the measured cost after it. A policy counter, not an invoice.

The bundled tables and fixtures are **artificial** (currency XTS, the ISO 4217 code reserved for testing).

```bash
dotnet run --project src/Ch22.CostCapacity/Ch22.CostModel                      # both fixtures, table A
dotnet run --project src/Ch22.CostCapacity/Ch22.CostModel -- --prices data/prices.artificial-b.json
dotnet run --project src/Ch22.CostCapacity/Ch22.CostModel -- --usage <file>    # e.g. LoadProfile --usage-out
dotnet run --project src/Ch22.CostCapacity/Ch22.CostModel -- --self-test
```

The self-test reproduces two defects before the fix: per-call cost stays flat while retries and abandonment more than double the cost per accepted task, and a check-then-spend budget lets 16 concurrent tasks overspend a limit that reservations hold.
