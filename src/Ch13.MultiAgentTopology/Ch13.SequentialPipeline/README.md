# Chapter 11 -- Sequential Pipeline

Demonstrates the simplest multi-agent pattern: two agents in sequence. A Translator agent converts French text to English, then a Summarizer agent condenses the translation into a brief summary.

## Key Concepts

| Concept | Description |
|---------|-------------|
| Sequential chaining | Output of agent 1 becomes the input to agent 2 |
| Distinct personas | Each agent has its own instructions and role |
| `ChatClientAgent` | Lightweight agent backed by an `IChatClient` |
| Session isolation | Each agent maintains its own conversation session |

## Running

```bash
dotnet run --project src/Ch11.MultiAgent/Ch11.SequentialPipeline
```
