// ============================================================================
// Uncertain outcome: reconcile, never retry blindly
// ----------------------------------------------------------------------------
// The reimbursement is committed locally (see Ch08.IdempotentCommit) and an
// outbox row says "pay the employee". The worker calls the payment provider.
// The provider accepts the payment, then the response is lost: a timeout. Did
// the money move? The worker cannot know, so it records OutcomeUnknown.
//
// A later run, in another process, resumes: it asks the provider for the
// payment by the stable reference (the operation id). Found -> Reconciled.
// Not found -> sent again with the SAME reference, which the provider
// deduplicates. One simulated effect, one operation id, from start to end.
//
//   dotnet run                                  both steps in this process
//   dotnet run -- submit --store <dir> [--fault accept-then-timeout|lost-request|crash-after-accept]
//   dotnet run -- resume --store <dir>          a NEW process reconciles
//   dotnet run -- --self-test                   submit and resume as two child
//                                               processes; RESULT n/n
// The provider is simulated (its journal is a second SQLite file). Nothing
// here moves real money, and "exactly once" is not promised for an arbitrary
// provider: it holds only because this one deduplicates on the reference.
// ============================================================================

using System.Diagnostics;
using Ch08.UncertainOutcome;

if (args.Contains("--self-test")) return TwoProcessSelfTest.Run();

string[] positional = [.. args.Where(a => !a.StartsWith("--", StringComparison.Ordinal))];
string store = Option(args, "--store") ?? Path.Combine(Path.GetTempPath(), "ch08-uncertain-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(store);
string appDb = Path.Combine(store, "app.db");
string journal = Path.Combine(store, "provider.db");

switch (positional.FirstOrDefault())
{
    case "submit":
    {
        ProviderFault fault = ParseFault(Option(args, "--fault"));
        Guid operation = Option(args, "--operation") is { } text ? Guid.Parse(text) : Guid.NewGuid();
        Console.WriteLine($"=== Uncertain outcome: submit (fault: {fault}) ===");
        var worker = new DeliveryWorker(appDb, new FakePaymentProvider(journal, fault));
        DeliveryStatus status = await worker.DeliverAsync(
            new Delivery("contoso", operation, "E-1001", "100", "EUR"), CancellationToken.None);
        Console.WriteLine($"Operation: {operation}");
        Console.WriteLine($"Status: {status}");
        Console.WriteLine($"Provider effects: {new FakePaymentProvider(journal).EffectCount()}");
        return 0;
    }
    case "resume":
    {
        Console.WriteLine("=== Uncertain outcome: resume ===");
        var worker = new DeliveryWorker(appDb, new FakePaymentProvider(journal));
        IReadOnlyList<ReconcileResult> results = await worker.ResumeAsync(CancellationToken.None);
        if (results.Count == 0) Console.WriteLine("Nothing to reconcile.");
        foreach (ReconcileResult r in results)
            Console.WriteLine($"Operation: {r.OperationId} -> {r.Status} ({r.How})");
        Console.WriteLine($"Provider effects: {new FakePaymentProvider(journal).EffectCount()}");
        return results.Any(r => r.Status == DeliveryStatus.OutcomeUnknown) ? 2 : 0;
    }
    case null:
    {
        Console.WriteLine("=== Uncertain outcome (both steps in one process) ===\n");
        Guid operation = Guid.NewGuid();
        var worker = new DeliveryWorker(appDb, new FakePaymentProvider(journal, ProviderFault.AcceptThenTimeout));
        DeliveryStatus first = await worker.DeliverAsync(
            new Delivery("contoso", operation, "E-1001", "100", "EUR"), CancellationToken.None);
        Console.WriteLine($"Deliver {operation}: {first}");
        foreach (ReconcileResult r in await worker.ResumeAsync(CancellationToken.None))
            Console.WriteLine($"Resume  {r.OperationId}: {r.Status} ({r.How})");
        Console.WriteLine($"Provider effects: {new FakePaymentProvider(journal).EffectCount()}");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(store, recursive: true); } catch (IOException) { }
        return 0;
    }
    default:
        Console.Error.WriteLine("Usage: [submit --store <dir> [--fault <fault>] [--operation <id>] | resume --store <dir>] | --self-test");
        return 64;
}

static string? Option(string[] args, string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static ProviderFault ParseFault(string? value) => value switch
{
    null or "none" => ProviderFault.None,
    "accept-then-timeout" => ProviderFault.AcceptThenTimeout,
    "lost-request" => ProviderFault.LostRequest,
    "crash-after-accept" => ProviderFault.CrashAfterAccept,
    _ => throw new ArgumentException($"unknown fault '{value}'"),
};

// ----------------------------------------------------------------------------
// --self-test: submit and resume run as separate child processes (dotnet exec
// of this same assembly), so the reconciliation really crosses a restart.
// ----------------------------------------------------------------------------
internal static class TwoProcessSelfTest
{
    static int s_passed, s_total;

    public static int Run()
    {
        Console.WriteLine("=== Uncertain outcome: two-process self-test ===");
        string root = Path.Combine(Path.GetTempPath(), "ch08-uncertain-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            // 1. Provider accepts, response lost; a second process reconciles.
            Console.WriteLine("\n-- accept-then-timeout --");
            string a = Store(root, "accept");
            const string opA = "3f1c2b7e-8a4d-4e6f-9b0a-1c2d3e4f5a6b";
            Child submit = RunChild("submit", "--store", a, "--fault", "accept-then-timeout", "--operation", opA);
            Expect("submit records OutcomeUnknown after the timeout", submit.ExitCode == 0 &&
                submit.Output.Contains("Status: OutcomeUnknown"));
            Expect("the provider journal already holds one payment", Effects(a) == 1);
            Child resume = RunChild("resume", "--store", a);
            Expect("resume runs in a different process", resume.Pid != submit.Pid && resume.Pid != Environment.ProcessId);
            Expect("resume finds the payment by reference and reconciles it",
                resume.ExitCode == 0 && resume.Output.Contains($"Operation: {opA} -> Reconciled"));
            Delivery? reconciled = Worker(a).Load("contoso", Guid.Parse(opA));
            Expect("same operation id, stored payment id, still one effect",
                reconciled is { Status: DeliveryStatus.Reconciled, PaymentId: not null } && Effects(a) == 1);
            Child again = RunChild("resume", "--store", a);
            Expect("a second resume finds nothing to do", again.ExitCode == 0 &&
                again.Output.Contains("Nothing to reconcile.") && Effects(a) == 1);

            // 2. The request never arrived; resume sends it with the same reference.
            Console.WriteLine("\n-- lost-request --");
            string b = Store(root, "lost");
            const string opB = "9a8b7c6d-5e4f-4a3b-8c2d-1e0f9a8b7c6d";
            Child submitB = RunChild("submit", "--store", b, "--fault", "lost-request", "--operation", opB);
            Expect("submit records OutcomeUnknown and no payment exists yet",
                submitB.Output.Contains("Status: OutcomeUnknown") && Effects(b) == 0);
            Child resumeB = RunChild("resume", "--store", b);
            Expect("resume finds no payment, sends again with the same reference: one effect",
                resumeB.Output.Contains($"Operation: {opB} -> Delivered") && Effects(b) == 1);

            // 3. The worker process dies after the provider accepted.
            Console.WriteLine("\n-- crash-after-accept --");
            string c = Store(root, "crash");
            const string opC = "5b6c7d8e-9f0a-4b1c-8d2e-3f4a5b6c7d8e";
            Child crashed = RunChild("submit", "--store", c, "--fault", "crash-after-accept", "--operation", opC);
            Expect($"the worker died (exit {crashed.ExitCode}) with the delivery still marked Sending",
                crashed.ExitCode == FakePaymentProvider.CrashExitCode &&
                Worker(c).Load("contoso", Guid.Parse(opC))?.Status == DeliveryStatus.Sending && Effects(c) == 1);
            Child resumeC = RunChild("resume", "--store", c);
            Expect("resume reconciles the Sending row: one effect, same operation id",
                resumeC.Output.Contains($"Operation: {opC} -> Reconciled") && Effects(c) == 1);

            // 4. Defect reproduction: the blind retry this design replaces.
            Console.WriteLine("\n-- Defect reproduction --");
            string d = Store(root, "blind");
            var provider = new FakePaymentProvider(Path.Combine(d, "provider.db"), ProviderFault.AcceptThenTimeout);
            var request = new PaymentRequest(Guid.NewGuid().ToString("D"), "E-1001", "100", "EUR");
            try { provider.PayAsync(request, CancellationToken.None).GetAwaiter().GetResult(); }
            catch (TimeoutException)
            {
                // "It failed, try again" with a fresh reference: a second payment.
                provider.PayAsync(request with { Reference = Guid.NewGuid().ToString("D") }, CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
            Expect($"DEFECT REPRODUCED: a blind retry with a new reference pays twice ({provider.EffectCount()} effects)",
                provider.EffectCount() == 2);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
        Console.WriteLine($"\nRESULT {s_passed}/{s_total}");
        return s_passed == s_total ? 0 : 1;
    }

    static string Store(string root, string name)
    {
        string dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    static long Effects(string store) => new FakePaymentProvider(Path.Combine(store, "provider.db")).EffectCount();

    static DeliveryWorker Worker(string store) =>
        new(Path.Combine(store, "app.db"), new FakePaymentProvider(Path.Combine(store, "provider.db")));

    static void Expect(string name, bool ok)
    {
        s_total++;
        if (ok) s_passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    }

    sealed record Child(int ExitCode, string Output, int Pid);

    /// <summary>Runs this assembly again as a separate process: dotnet exec &lt;dll&gt; args.</summary>
    static Child RunChild(params string[] childArgs)
    {
        ProcessStartInfo psi = new(DotnetHost())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(typeof(TwoProcessSelfTest).Assembly.Location);
        foreach (string arg in childArgs) psi.ArgumentList.Add(arg);

        using Process process = Process.Start(psi)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(120)))
        {
            process.Kill(entireProcessTree: true);
            return new Child(-1, "timed out", process.Id);
        }
        string output = stdout.Result + stderr.Result;
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            Console.WriteLine($"    | {line.TrimEnd('\r')}");
        return new Child(process.ExitCode, output, process.Id);
    }

    static string DotnetHost()
    {
        string? current = Environment.ProcessPath;
        if (current is not null &&
            Path.GetFileNameWithoutExtension(current).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return current;
        string? host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        return !string.IsNullOrEmpty(host) && File.Exists(host) ? host : "dotnet";
    }
}
