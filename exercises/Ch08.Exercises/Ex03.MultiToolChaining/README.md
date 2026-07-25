# Exercise 03 — Multi-Tool Chaining (Challenge)

## Prompt

Build an agent with five tools backed by a pretend e-commerce service: `SearchProducts`, `GetProductDetails`, `CheckInventory`, `PlaceOrder`, and `TrackOrder`. Wire all five up. Give the agent a three-step scenario — "I want to order 2 blue widgets, check they're in stock at the warehouse in Seattle, and track the shipment afterward" — and watch the agent chain the tools. Instrument your tools with `ILogger` to confirm the sequence.

## Hints

- This is a scale-up of `Ch08.DatabaseLookupTool`: a DI-registered tool class with injected dependencies (here, `ILogger<EcommerceTools>`).
- Don't over-engineer the in-memory data — five hardcoded products and three warehouses (Seattle, Denver, Atlanta) are enough to see the chaining behavior.
- The logging is what makes the exercise worthwhile: you should see four or five tool calls in the log for the single user question.

## Expected Outcome

For the single scenario question, the log shows the chain, e.g.:

```
info: EcommerceTools[0] Tool call: SearchProducts query='blue widget'
info: EcommerceTools[0] Tool call: CheckInventory productId=1 warehouse=Seattle
info: EcommerceTools[0] Tool call: PlaceOrder productId=1 quantity=2 warehouse=Seattle
info: EcommerceTools[0] Tool call: TrackOrder orderId=ORD-0001
```

## Key Concepts

- Five-tool agent over a pretend e-commerce service
- `ILogger` instrumentation to observe the tool-call sequence
- Tool design for composability (each result feeds the next call)
- DI-backed tool class with constructor injection
