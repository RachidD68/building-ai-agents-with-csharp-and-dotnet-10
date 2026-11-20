# Exercise 03 — Invoice Validator (Challenge)

## Prompt

Extract an invoice with `TryRunStructuredAsync<T>()` and check what it means, not
only its shape. Add three rules: the invoice is in the currency its vendor bills in,
every amount and the exchange rate are positive, and the line amounts times the rate
equal the total within 0.01. Every outcome must be explicit: accepted, rejected with
the reason named, missing fields, or failed.

## Hints

- Make every amount `decimal?` with `[JsonNumberHandling(Strict)]`: an absent total is
  "missing", never 0, and a string amount is a shape error.
- Keep the vendor-to-currency table in code. The model reports the currency; it does not
  decide which one is right.
- Compare with a tolerance (`Math.Abs(expected - total) > 0.01m`), and print the numbers
  with the invariant culture.
- Test offline with the chapter's `CannedChatClient`: canned replies, no model.

## Acceptance

An offline suite of at least 8 canned cases prints `RESULT n/n`, and at least 2 invoices
that are schema-valid but domain-invalid are rejected with the reason named.

## Run

```bash
dotnet run                    # one invoice through the model
dotnet run -- --self-test     # offline suite, RESULT n/n
```

## Key Concepts

- `TryRunStructuredAsync<T>()` for the shape, a validator for the meaning
- Explicit outcomes instead of defaults
- Cross-field rules with a tolerance
