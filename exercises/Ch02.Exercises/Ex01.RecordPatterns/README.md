# Exercise 01 — Record Patterns (Basic)

## Prompt

Given a list of `LlmResponse` records, write a **switch expression** that classifies each one into `"low-cost"`, `"medium-cost"`, `"high-cost"`, or `"error"` based on status code and token usage. Use **property patterns** and **relational patterns**.

## Hints

- Start from the `LlmResponse` switch in the companion `Program.cs` (`src/Ch02.CSharpEssentials/Ch02.ConsoleApp`). You're extending the same shape with two more branches and a cost threshold.
- Property patterns reach into nested members: `{ StatusCode: 200, Usage.TotalTokens: < 500 }`
- Relational patterns combine with `and`: `>= 500 and < 2_000`
- Anything that is not a clean 200 (429, 5xx, ...) falls through to `"error"`.

## Expected Outcome

```
=== Exercise: Record Patterns — LlmResponse Cost Classifier ===

  Status   In Tok  Out Tok    Total   Category
  ------ -------- -------- --------   -----------
     200      120       80      200   low-cost
     200      310      145      455   low-cost
     200      900      400    1,300   medium-cost
     200    1,500      480    1,980   medium-cost
     200    3,200    1,800    5,000   high-cost
     200    9,000    2,500   11,500   high-cost
     429        0        0        0   error
     500        0        0        0   error
     503        0        0        0   error

--- Category Summary ---

  low-cost     2 response(s)
  medium-cost  2 response(s)
  high-cost    2 response(s)
  error        3 response(s)

=== Exercise complete! ===
```

## Key Concepts

- Records with value equality and computed properties (`TotalTokens`)
- Switch expressions with property patterns (including nested `Usage.TotalTokens`)
- Relational patterns and the `and` combinator for threshold branching
- A discard arm (`_`) as the error catch-all
