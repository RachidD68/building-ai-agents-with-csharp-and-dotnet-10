# Exercise 01: Cost Calculator (Basic)

## Prompt

The first-edition simulator kept a model catalogue with rates in the code and
modelled reasoning as a multiplier on visible output. Replace both:

1. Load two **artificial** price tables from `data/` (currency, effective date,
   unit, one rate per model). The formula must contain no provider name and no
   price literal.
2. Load one usage fixture: each run is a context strategy (uncached, stable
   prefix cached, a sliding window plus caching) on one model, with the
   `UsageDetails` it measured per conversation and how many of its
   conversations were accepted.
3. Price every run under both tables: per conversation, per month at the
   fixture's volume, and **per accepted conversation**.
4. Read reasoning cost from the measured `ReasoningTokenCount`, which is part of
   `OutputTokenCount`; cached tokens are part of `InputTokenCount`.

## Accepted when

- the totals change when you switch tables;
- the formula contains no provider name or price literal;
- a run with zero accepted conversations prints `undefined`, not `0`.

## Expected outcome

`dotnet run -- --self-test` ends with `RESULT 6/6`. `dotnet run` prints, for table-1:

```
table-1 (XTS, effective 2026-11-01, artificial)
  Strategy                   Model     Per conversation      Monthly  Accepted   Per accepted
  uncached                   model-r           0.022400       336.00     22/25       0.025455
  stable prefix cached       model-r           0.018080       271.20     22/25       0.020545
  window 3 turns + cached    model-r           0.012480       187.20     20/25       0.015600
  window 3 turns + cached    model-a           0.001520        22.80     15/25       0.002533
  window 1 turn + cached     model-a           0.001060        15.90      0/25      undefined
```

The rates and the fixture are made up (XTS is the ISO 4217 code reserved for
testing). They show the arithmetic; they are not prices.

## Key concepts

- Price tables as dated data, never constants
- `UsageDetails`: cached input inside the input count, reasoning inside the output count
- Caching changes cost, not quality; a tighter window can cost quality
- Cost per accepted conversation, undefined when nothing was accepted
