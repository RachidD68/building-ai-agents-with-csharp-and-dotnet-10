# Exercise 01 — Text Processing Workflow (Beginner)

## Prompt

Build a workflow with 3 executors: CleanText (normalize whitespace) -> CountWords -> FormatReport. Use WorkflowBuilder.

## Key Concepts

- `WorkflowBuilder` for graph construction
- `ExecutorBindingExtensions.BindAsExecutor()` for wrapping functions
- `AddEdge()` for connecting nodes
- `InProcessExecution.Default` for running workflows
- `WorkflowVisualizer.ToMermaidString()` for graph visualization
