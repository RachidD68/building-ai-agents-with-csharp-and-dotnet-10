# Exercise 02 — Todo Manager Tool (Intermediate)

## Prompt

Build an agent with CRUD tools for a todo list: AddTodo, ListTodos, CompleteTodo, DeleteTodo. The agent manages the list through natural language.

## Hints

- Use closures over a shared `List<TodoItem>` to maintain state.
- `AIFunctionFactory.Create()` can wrap lambdas.
- Use `AgentSession` for multi-turn conversation memory.

## Expected Outcome

```
You: Add three items: buy groceries, write code, and read a book
  [Tool] AddTodo: #1 "buy groceries"
  [Tool] AddTodo: #2 "write code"
  [Tool] AddTodo: #3 "read a book"
Agent: I've added three items to your todo list...

You: I finished writing code, mark it done
  [Tool] CompleteTodo: #2 "write code"
Agent: Done! I've marked "write code" as completed.
```

## Key Concepts

- Tool registration with stateful closures
- CRUD operations via natural language
- Multi-turn tool interaction
- Agent as a natural language interface to data
