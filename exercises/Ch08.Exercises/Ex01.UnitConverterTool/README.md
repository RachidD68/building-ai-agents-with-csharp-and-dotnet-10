# Exercise 01 — Unit Converter Tool (Basic)

## Prompt

Build a single-tool agent with a `ConvertTemperature(double value, string from, string to)` method that converts between Celsius, Fahrenheit, and Kelvin. Annotate with `[Description]`. Verify the agent uses the tool for questions like "Convert 100°F to Celsius" and "What's 0°C in Kelvin?".

## Hints

- Start from the `Ch08.WeatherTool` project and replace the weather method with your converter.
- Add `[Description]` attributes on the method **and each parameter** — list the valid units explicitly.
- Use `AIFunctionFactory.Create()` to wrap the method as a tool.

## Expected Outcome

```
You: Convert 100°F to Celsius.
  [Tool] ConvertTemperature(100, Fahrenheit, Celsius) → 100°F = 37.78°C
Agent: 100°F is 37.78°C...

You: What's 0°C in Kelvin?
  [Tool] ConvertTemperature(0, Celsius, Kelvin) → 0°C = 273.15 K
Agent: 0°C equals 273.15 Kelvin...
```

## Key Concepts

- Single-tool agent with `AIFunctionFactory.Create()`
- `[Description]` attributes on the method and every parameter
- Explicit valid-unit lists in parameter descriptions
- Graceful degradation: unknown units return an error string, not an exception
