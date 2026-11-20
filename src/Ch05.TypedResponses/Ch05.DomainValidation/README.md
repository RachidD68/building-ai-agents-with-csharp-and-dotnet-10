# Ch05.DomainValidation — Shape, Then Meaning

An employee pastes "Taxi 64,80 EUR, 12 November, airport to the Fabrikam office".
The typed call turns it into a record; that only proves the JSON had the right
shape. This sample adds the second check, the domain rules, and makes every
result an explicit state:

| State | When |
|---|---|
| `Valid` | the draft deserialized and passed every rule |
| `Invalid(reasons)` | it deserialized but breaks rules, each named (`currency_mismatch`, `currency_not_iso`, `amount_not_positive`, `lines_disagree_with_total`, `line_amount_missing`, `amount_not_in_source`) |
| `Missing(fields)` | the amount or the currency is absent; never filled in with 0 or a guess |
| `Refused` | the provider's content filter stopped the reply (`ChatFinishReason.ContentFilter`) |
| `Truncated` | the provider stopped the reply at its output limit (`ChatFinishReason.Length`) |
| `Unreadable` | the reply could not be read as a claim, even after one retry |

The claim is only data. Nothing here pays, books or sends anything.

## The pieces

- `ExpenseClaim` — the minimal record. A missing `amount` key becomes `0m`, and a
  USD or negative claim deserializes without complaint; the self-test reproduces each.
- `ClaimDraft` — what the validated pipeline asks for: nullable fields, so "absent"
  stays absent; `[JsonNumberHandling(Strict)]` on amounts, so `"64,80"` as a string
  is a shape error, not a number to guess at.
- `ClaimValidator` — the ladder: presence, then currency and sign, then line totals,
  then provenance (the total must be a number written in the text, read the same way
  under any culture, so `64,80` is 64.80 and never 6480). The self-reported confidence
  is printed and never read by a rule.
- `ClaimPipeline` — `TryRunStructuredAsync<T>` from `Ch05.RobustExtraction`, then the
  validator. A retry is spent on shape only; a truncated or filtered reply is not retried.

## What the provider enforces

`RunAsync<T>` sends a JSON-schema response format named after the type (the self-test
checks it on a fake client). Whether the model is held to that schema depends on the
connector and the model; the domain rules run either way. With Ollama, the OllamaSharp
connector reads the schema and maps Ollama's `done_reason` to a finish reason; it has no
content-filter state, so a local model's refusal arrives as prose and ends `Unreadable`.

## Run

```bash
dotnet run                    # three claims through the model, minimal call then pipeline
dotnet run -- --self-test     # offline: fake chat client, RESULT n/n, exit code 1 on failure
```
