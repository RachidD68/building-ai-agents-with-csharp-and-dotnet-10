# Exercise 03 — Multi-Tool Chaining (Challenge)

## Prompt

Build an agent with five tools backed by a pretend e-commerce service: `SearchProducts`, `GetProductDetails`, `CheckInventory`, `PlaceOrder`, and `TrackOrder`. Wire all five up. Give the agent a three-step scenario — "I want to order 2 blue widgets, check they're in stock at the warehouse in Seattle, and track the shipment afterward" — and watch the agent chain the tools. Instrument your tools with `ILogger` to confirm the sequence.

## Hints

- This is a scale-up of `Ch07.DatabaseLookupTool`: a DI-registered tool class with injected dependencies (here, `ILogger<EcommerceTools>`).
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

## The iteration cap

The invocation layer is built explicitly, as `Ch07.LoopControl` does: `UseFunctionInvocation` with `MaximumIterationsPerRequest = 8` (`ChainAgent.MaxIterations`) and `UseProvidedChatClientAsIs = true`, so the agent adds no default layer of its own. The chain needs four or five round trips; eight leaves room for one retry.

## Self-test

`dotnet run -- --self-test` needs no model. A scripted chat client asks for SearchProducts, CheckInventory, PlaceOrder and TrackOrder in turn, and the `ILogger` lines show that order; a second scripted client never stops asking for CheckInventory, and the run stops after 8 tool calls and returns the model's text answer, with no exception. It ends with `RESULT 5/5`.

`PlaceOrder` only changes an in-memory dictionary. It is a simulated write: the authorized-actions chapter shows how to make a real one safe.
