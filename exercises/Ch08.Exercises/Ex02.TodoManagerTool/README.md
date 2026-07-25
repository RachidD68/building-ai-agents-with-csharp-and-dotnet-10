# Exercise 02 — Todo Manager Tool (Intermediate)

## Prompt

Build a stateful todo-list agent with three tools: `AddTodo(string title, string priority)`, `ListTodos()`, and `CompleteTodo(int id)`. Back the tools with a simple in-memory `TodoRepository` registered in DI. Run a multi-turn conversation that adds several items, lists them, and marks some complete.

## Hints

- The DI pattern from `Ch08.DatabaseLookupTool` is the template.
- Keep the repository a singleton for this exercise.
- Register the tool class in DI and resolve it before wrapping its methods with `AIFunctionFactory.Create()`.
- Use one `AgentSession` for the whole conversation so the agent remembers earlier turns.

## Expected Outcome

```
You: Add three todos: buy groceries with high priority, ...
  [Tool] AddTodo: #1 "buy groceries" (high)
  [Tool] AddTodo: #2 "write the chapter review" (medium)
  [Tool] AddTodo: #3 "book a dentist appointment" (low)
Agent: I've added three items to your todo list...

You: I bought the groceries and booked the dentist — mark both of those done.
  [Tool] CompleteTodo: #1 "buy groceries"
  [Tool] CompleteTodo: #3 "book a dentist appointment"
Agent: Done! Both items are marked complete.
```

## Key Concepts

- `TodoRepository` registered as a DI singleton
- Tool class (`TodoTools`) with constructor-injected dependencies
- `AIFunctionFactory.Create()` over DI-resolved instance methods
- Multi-turn tool interaction on a single `AgentSession`
