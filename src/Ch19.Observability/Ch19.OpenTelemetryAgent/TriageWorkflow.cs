using Microsoft.Agents.AI.Workflows;

namespace Ch19.Observability;

/// <summary>
/// A one-step workflow, just enough to see what workflow telemetry emits at
/// MAF 1.28: WithOpenTelemetry() on the builder turns the spans on.
/// </summary>
public static class TriageWorkflow
{
    public const string Name = "ContosoClaimTriage";

    public static Workflow Build()
    {
        ExecutorBinding classify = new ClassifyExecutor().BindExecutor();
        return new WorkflowBuilder(classify)
            .WithOutputFrom(classify)
            .WithName(Name)
            .WithOpenTelemetry()
            .Build();
    }

    public static async Task<string?> RunAsync(string claim)
    {
        Run run = await InProcessExecution.RunAsync(Build(), claim, sessionId: Guid.NewGuid().ToString());
        return run.OutgoingEvents.OfType<WorkflowOutputEvent>().Select(e => e.Data as string).LastOrDefault();
    }
}

[YieldsOutput(typeof(string))]
internal sealed class ClassifyExecutor() : Executor<string>("classify")
{
    public override ValueTask HandleAsync(string claim, IWorkflowContext context, CancellationToken ct) =>
        context.YieldOutputAsync(claim.Contains("dinner", StringComparison.OrdinalIgnoreCase) ? "meals" : "other", ct);
}
