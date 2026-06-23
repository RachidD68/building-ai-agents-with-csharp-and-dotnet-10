# Chapter 8 -- Tools and Function Calling (Calculator Tool)

Demonstrates multiple tool registration and tool chaining. The agent calls several calculator tools in sequence to answer multi-step math questions like "What's 15% tip on a $47.50 bill in euros?"

## Key Concepts

| Concept | Description |
|---------|-------------|
| Multiple tools | Five separate tools: Add, Subtract, Multiply, Divide, ConvertUnits |
| Tool chaining | Agent calls multiple tools in sequence for multi-step reasoning |
| Static methods | All tool methods are static with `[Description]` on method and parameters |
| Low temperature | `Temperature = 0.1f` for deterministic math results |

## Running

```bash
dotnet run --project src/Ch08.ToolsFunctionCalling/Ch08.CalculatorTool
```
