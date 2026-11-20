using Ch08.UncertainOutcome;

namespace Ch15.ExternalEffects;

/// <summary>
/// Named points in a payout attempt. A hook does nothing by default; the
/// restart tests pause a real process at one of them and kill it there.
/// </summary>
public static class StepPoints
{
    /// <summary>Lease held, workflow restored, nothing committed for this attempt yet.</summary>
    public const string BeforeCommit = "before-commit";
    /// <summary>Delivery row is Sending; the request has not reached the provider.</summary>
    public const string BeforeEffect = "before-effect";
    /// <summary>The provider wrote the payment to its journal; the reply has not reached the worker.</summary>
    public const string AfterAccept = "after-accept";
    /// <summary>The business record says Paid; the checkpoint after the payout step is not written yet.</summary>
    public const string AfterRecord = "after-record";

    public static readonly IReadOnlyList<string> All = [BeforeCommit, BeforeEffect, AfterAccept, AfterRecord];
}

public interface IStepHook
{
    ValueTask ReachAsync(string point);
}

public sealed class NoHook : IStepHook
{
    public ValueTask ReachAsync(string point) => ValueTask.CompletedTask;
}

/// <summary>
/// --pause-at: at the named point, print "PAUSED at &lt;point&gt;" and wait for a
/// line on stdin. The test harness kills the process while it waits (or sends
/// a line to let it go on); at a terminal, press Enter to continue or close
/// the window to kill it.
/// </summary>
public sealed class StdinPause(string target) : IStepHook
{
    public ValueTask ReachAsync(string point)
    {
        if (point != target) return ValueTask.CompletedTask;
        Console.WriteLine($"PAUSED at {point} (pid {Environment.ProcessId})");
        Console.Out.Flush();
        Console.In.ReadLine();
        Console.WriteLine($"continuing after {point}");
        return ValueTask.CompletedTask;
    }
}

/// <summary>In-process hook for the self-test: runs an action when a point is reached.</summary>
public sealed class ActionHook(string target, Func<ValueTask> action) : IStepHook
{
    public ValueTask ReachAsync(string point) => point == target ? action() : ValueTask.CompletedTask;
}

/// <summary>
/// Wraps the simulated provider so a test can stop on either side of the
/// provider's own write. Nothing else changes: the inner provider pays, dedupes
/// by reference and simulates its faults exactly as in Ch08.
/// </summary>
public sealed class HookedProvider(IPaymentProvider inner, IStepHook hook) : IPaymentProvider
{
    public async Task<PaymentReceipt> PayAsync(PaymentRequest request, CancellationToken ct)
    {
        await hook.ReachAsync(StepPoints.BeforeEffect);
        PaymentReceipt receipt = await inner.PayAsync(request, ct);
        await hook.ReachAsync(StepPoints.AfterAccept);
        return receipt;
    }

    public Task<PaymentReceipt?> FindByReferenceAsync(string reference, CancellationToken ct) =>
        inner.FindByReferenceAsync(reference, ct);
}
