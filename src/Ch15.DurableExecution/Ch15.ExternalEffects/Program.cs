// ============================================================================
// External Effects: a workflow step that pays, and the windows around it
// ----------------------------------------------------------------------------
// Ch08.UncertainOutcome reconciled one uncertain provider call. Inside a
// workflow there are more windows, because the checkpoint lags the effect:
//
//   business record -> provider call -> provider journal -> local record -> checkpoint
//                   ^               ^                    ^
//            before-effect     after-accept         after-record
//
// A worker that dies in any of them restarts from the checkpoint taken BEFORE
// the payout step, so the step runs again. This sample makes running again
// safe and shows each outcome: exactly one effect, or an explicit Unknown
// that reconciliation settles. The simulated provider and the reconciler are
// Ch08.UncertainOutcome's (FakePaymentProvider, DeliveryWorker), unchanged.
//
//   dotnet run -- start <claim> [--amount 64.80] [--pause-at <point>] [--fault <fault>]
//   dotnet run -- resume <claim>             a new process: lease, restore, run again
//   dotnet run -- cancel <claim>             the requester asks to cancel
//   dotnet run -- status <claim>             business record, delivery, journal
//   dotnet run -- --self-test                every window in process (offline)
// Points: before-commit, before-effect, after-accept, after-record. Faults:
// accept-then-timeout, lost-request. Options: --store <dir>, --owner <name>,
// --lease-seconds <n> (default 30, renewed every third of it), --no-renew (a frozen
// worker: the lease is not renewed), --naive (the step without its safeguards).
// The kill-at-each-point proof with real processes is Ch15.RestartTests.
// Exit codes: 0 Paid or Cancelled, 4 refused (stale lease, conflict),
// 5 nothing done (lease held, unknown claim), 6 Unknown (reconcile later).
// ============================================================================

using System.Globalization;
using Ch08.UncertainOutcome;
using Ch15.ExternalEffects;

if (args.Contains("--self-test")) return await ExternalEffectsSelfTest.RunAsync();

string[] valued = ["--store", "--amount", "--employee", "--pause-at", "--fault", "--lease-seconds", "--owner"];
string[] words = Positional();
string storePath = Option("--store") ?? Path.Combine(Path.GetTempPath(), "AgenticAI", "Ch15.ExternalEffects");
string claim = words.ElementAtOrDefault(1) ?? "CLM-2001";
PayoutHost host = new(storePath);

IStepHook hook = Option("--pause-at") is { } point
    ? StepPoints.All.Contains(point) ? new StdinPause(point) : throw new ArgumentException($"unknown point {point}")
    : new NoHook();
ProviderFault fault = Option("--fault") switch
{
    null => ProviderFault.None,
    "accept-then-timeout" => ProviderFault.AcceptThenTimeout,
    "lost-request" => ProviderFault.LostRequest,
    string other => throw new ArgumentException($"unknown fault {other}"),
};
TimeSpan lease = TimeSpan.FromSeconds(double.Parse(Option("--lease-seconds") ?? "30", CultureInfo.InvariantCulture));
string owner = Option("--owner") ?? $"worker-{Environment.ProcessId}";

switch (words.FirstOrDefault())
{
    case "approve":
        Approve();
        return 0;
    case "start":
        Approve();
        return PayoutHost.ExitCode(await host.RunAsync(claim, owner, lease, hook, fault, args.Contains("--naive"), renew: !args.Contains("--no-renew")));
    case "resume":
        Console.WriteLine($"=== External Effects: resume {claim} ===");
        return PayoutHost.ExitCode(await host.RunAsync(claim, owner, lease, hook, fault, args.Contains("--naive"), renew: !args.Contains("--no-renew")));
    case "cancel":
        host.Store.RequestCancel(claim);
        Console.WriteLine($"Cancel requested for {claim}; the payout is {host.Store.Load(claim)?.State ?? "unknown"}.");
        return 0;
    case "status":
        host.PrintStatus(claim);
        return 0;
    default:
        Console.Error.WriteLine("Usage: start|approve|resume|cancel|status <claim> [--store dir] [--pause-at point] " +
                                "[--fault accept-then-timeout|lost-request] [--lease-seconds n] [--no-renew] [--owner name] [--naive] | --self-test");
        return 2;
}

void Approve()
{
    Console.WriteLine($"=== External Effects: start {claim} ===");
    PayoutRow row = host.Store.Approve(claim, PayoutHost.Tenant, Option("--employee") ?? "E-1001",
        decimal.Parse(Option("--amount") ?? "64.80", CultureInfo.InvariantCulture), "EUR");
    Console.WriteLine($"Approved: {row.ClaimId} {row.Amount} {row.Currency}, operation {row.OperationId} (issued once, here)");
}

string? Option(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

string[] Positional()
{
    List<string> list = [];
    for (int i = 0; i < args.Length; i++)
    {
        if (valued.Contains(args[i])) i++;
        else if (!args[i].StartsWith("--", StringComparison.Ordinal)) list.Add(args[i]);
    }
    return [.. list];
}
