# Chapter 12 -- Conditional Workflow

Demonstrates conditional routing in the Workflow engine. A sentiment analyzer classifies messages as positive, negative, or neutral, then routes them to the appropriate handler using two approaches: typed conditional edges and switch-based routing.

## Key Concepts

| Concept | Description |
|---------|-------------|
| `AddEdge<T>` with condition | Typed predicate controls whether an edge activates |
| `AddSwitch` | Multi-branch routing with case predicates and a default |
| Sentiment analysis | Simple keyword-based classifier for routing decisions |
| Multiple output nodes | Different branches produce different types of responses |

## Running

```bash
dotnet run --project src/Ch12.Workflows/Ch12.ConditionalWorkflow
```
