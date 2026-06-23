# Exercise 03 — Multi-Tool Chaining (Challenge)

## Prompt

Build an agent with 4+ tools that must chain calls: CurrencyConverter, TaxCalculator, ShippingEstimator, PriceFormatter.

## Hints

- The LLM automatically decides the order of tool calls.
- Design tools with clear descriptions so the LLM chains them correctly.
- Each tool should return enough context for the next tool.

## Expected Outcome

For "What's the total cost of a $50 item shipped to France with tax?", the agent chains: CurrencyConverter -> TaxCalculator -> ShippingEstimator -> FormatPrice.

## Key Concepts

- Multi-tool chaining
- Tool design for composability
- Agent reasoning about tool call ordering
- Simulated external service APIs
