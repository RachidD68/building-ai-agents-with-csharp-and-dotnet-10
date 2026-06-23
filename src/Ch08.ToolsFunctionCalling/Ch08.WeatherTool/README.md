# Chapter 8 -- Tools and Function Calling (Weather Tool)

Demonstrates the full tool-calling loop with the Microsoft Agent Framework. The agent decides when to call the weather tool vs. answering directly from its training data.

## Key Concepts

| Concept | Description |
|---------|-------------|
| `AIFunctionFactory.Create()` | Wraps a C# method as an `AITool` the LLM can invoke |
| `[Description]` attributes | Tells the LLM what the tool does and what parameters mean |
| `ChatClientAgentOptions.Tools` | Registers tools with the agent |
| Graceful degradation | How the agent handles tool failures |

## Running

```bash
dotnet run --project src/Ch08.ToolsFunctionCalling/Ch08.WeatherTool
```
