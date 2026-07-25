# Exercise 02 — Review Pipeline (Intermediate)

## Prompt

Build a length-based conditional workflow with `AddSwitch`: a classifier
measures each review submission, then routes short, medium, and long input
down different review branches (quick / standard / deep).

## Key Concepts

- `AddSwitch` with `AddCase<T>` predicates and `WithDefault` for multi-branch routing
- Class-based executors (`Executor<TInput, TOutput>`) carrying a typed result record
- `WithOutputFrom()` on multiple terminal nodes
- Fresh workflow instance per run (deterministic "R0"-style id suffixes)

Mirrors the `AddSwitch` usage in `src/Ch13.Workflows/Ch13.ConditionalWorkflow`.
