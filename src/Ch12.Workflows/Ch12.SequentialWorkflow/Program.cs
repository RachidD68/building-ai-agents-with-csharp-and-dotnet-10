// ============================================================================
// Chapter 12 — Introduction to Workflows (Sequential Workflow)
// Demonstrates the Workflow engine from Microsoft.Agents.AI.Workflows.
//
// Key concepts:
//   - Class-based executors: derive from Executor<TInput, TOutput> and override
//     HandleAsync. The returned value is sent downstream automatically.
//   - BindExecutor(): turns an executor instance into a graph node binding.
//   - WorkflowBuilder: composes executor bindings into a directed graph.
//   - AddEdge(): connects executors in sequence.
//   - InProcessExecution: runs workflows in-process (RunAsync / RunStreamingAsync).
//   - WorkflowOutputEvent: captures workflow results.
//
// The workflow: Input → Uppercase → Reverse → Output
// ============================================================================

using Microsoft.Agents.AI.Workflows;

Console.WriteLine("=== Chapter 12: Sequential Workflow — Uppercase → Reverse ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// ---------------------------------------------------------------
// Step 1: Build the workflow graph
// ---------------------------------------------------------------
// Each executor is a small class deriving from Executor<TInput, TOutput>.
// BindExecutor() wraps an instance as a node; AddEdge connects them.

Workflow workflow = BuildWorkflow("main");

Console.WriteLine($"Workflow: {workflow.Name}");
Console.WriteLine($"Start executor: {workflow.StartExecutorId}\n");

// ---------------------------------------------------------------
// Step 2: Visualize the workflow graph
// ---------------------------------------------------------------
Console.WriteLine("--- Workflow Graph (Mermaid) ---");
Console.WriteLine(WorkflowVisualizer.ToMermaidString(workflow));
Console.WriteLine();

// ---------------------------------------------------------------
// Step 3: Execute with RunAsync (batch mode)
// ---------------------------------------------------------------
Console.WriteLine("--- RunAsync (Batch Execution) ---\n");

// Build a fresh workflow for each run (a built Workflow is single-owner).
Workflow batchWorkflow = BuildWorkflow("batch");

Run batchRun = await InProcessExecution.RunAsync(
    batchWorkflow, "hello world", sessionId: Guid.NewGuid().ToString(), cts.Token);

Console.WriteLine($"\nSession: {batchRun.SessionId}");
Console.WriteLine("Output events:");
foreach (WorkflowEvent evt in batchRun.OutgoingEvents)
{
    if (evt is WorkflowOutputEvent output)
    {
        Console.WriteLine($"  Final result: {output.Data}");
    }
}

// ---------------------------------------------------------------
// Step 4: Execute with RunStreamingAsync (streaming mode)
// ---------------------------------------------------------------
Console.WriteLine("\n--- RunStreamingAsync (Streaming Execution) ---\n");

Workflow streamWorkflow = BuildWorkflow("stream");

StreamingRun stream = await InProcessExecution.RunStreamingAsync(
    streamWorkflow, "streaming demo", sessionId: Guid.NewGuid().ToString(), cts.Token);

Console.WriteLine($"Session: {stream.SessionId}");
Console.WriteLine("Events:");
await foreach (WorkflowEvent evt in stream.WatchStreamAsync(cts.Token))
{
    switch (evt)
    {
        case ExecutorInvokedEvent invoked:
            Console.WriteLine($"  → Invoked [{invoked.ExecutorId}] with: {invoked.Data}");
            break;
        case ExecutorCompletedEvent completed:
            Console.WriteLine($"  ✓ Completed [{completed.ExecutorId}]: {completed.Data}");
            break;
        case WorkflowOutputEvent output:
            Console.WriteLine($"  ★ Output: {output.Data}");
            break;
    }
}

Console.WriteLine("\n=== Sequential Workflow demo complete! ===");

// ---------------------------------------------------------------
// Helper: build a fresh workflow instance.
// ---------------------------------------------------------------
// A built Workflow can only be owned by one runner, so we create a new
// instance (with unique executor ids) for each execution.
Workflow BuildWorkflow(string suffix)
{
    ExecutorBinding uppercase = new UppercaseExecutor($"uppercase-{suffix}").BindExecutor();
    ExecutorBinding reverse = new ReverseExecutor($"reverse-{suffix}").BindExecutor();

    return new WorkflowBuilder(uppercase)
        .AddEdge(uppercase, reverse)
        .WithOutputFrom(reverse)
        .WithName($"UppercaseReverse-{suffix}")
        .WithDescription("Converts text to uppercase then reverses it")
        .Build();
}

// ---------------------------------------------------------------
// Executors
// ---------------------------------------------------------------

/// <summary>Converts the input text to uppercase.</summary>
internal sealed class UppercaseExecutor(string id) : Executor<string, string>(id)
{
    public override ValueTask<string> HandleAsync(
        string input, IWorkflowContext context, CancellationToken ct)
    {
        string result = input.ToUpperInvariant();
        Console.WriteLine($"  [UppercaseExecutor] \"{input}\" → \"{result}\"");
        return ValueTask.FromResult(result);
    }
}

/// <summary>Reverses the input text.</summary>
internal sealed class ReverseExecutor(string id) : Executor<string, string>(id)
{
    public override ValueTask<string> HandleAsync(
        string input, IWorkflowContext context, CancellationToken ct)
    {
        string result = new(input.Reverse().ToArray());
        Console.WriteLine($"  [ReverseExecutor]   \"{input}\" → \"{result}\"");
        return ValueTask.FromResult(result);
    }
}
