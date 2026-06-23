# Exercise 01 — Unit Converter Tool (Beginner)

## Prompt

Create an agent with three unit converter tools: `ConvertTemperature`, `ConvertDistance`, `ConvertWeight`. Show multi-step reasoning.

## Hints

- Use `AIFunctionFactory.Create()` to wrap methods as tools.
- Add `[Description]` attributes on methods and parameters.
- The agent will decide which tool(s) to call based on the prompt.

## Expected Outcome

The agent calls the appropriate tool(s) and provides contextual answers about the conversions.

## Key Concepts

- `AIFunctionFactory.Create()` for tool registration
- `[Description]` attributes for LLM understanding
- Multi-tool agents
- Tool chaining by the LLM
