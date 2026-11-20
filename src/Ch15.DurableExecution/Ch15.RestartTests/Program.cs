// ============================================================================
// Restart Tests: kill a real worker at each failure window, then resume
// ----------------------------------------------------------------------------
// An in-memory checkpoint proves nothing about a restart, and neither does a
// comment that says "use file storage". Each case here starts a worker
// PROCESS, waits until it reports that it reached a named point, kills its
// process tree (no finally block runs, no lease is released), starts a new
// process, and then counts the simulated provider's journal rows.
//
//   1. Ch15.ExternalEffects: before-effect, after-accept, after-record;
//      Unknown settled later; a stale lease; a race; a cancel after it began
//   2. Ch15.DurableReview: prepare exits, a fresh process resumes; no static
//      field that can be reassigned in the three entry assemblies
//   3. Contoso v5: the same three kill points on the real claim flow
//
// Every model in these runs is scripted; nothing leaves the machine. The
// harness runs the BUILT assemblies of its own configuration: build the
// solution first. Accepts --self-test (same run) so the repository's
// self-test sweep finds it. Prints RESULT n/n; exit 0 only when all pass.
// ============================================================================

using Ch15.RestartTests;

_ = args.Contains("--self-test");   // the harness always runs everything offline
Console.WriteLine("=== Restart Tests (real processes, offline) ===");
int passed = 0, total = 0;
void Check(string name, bool ok)
{
    total++;
    if (ok) passed++;
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
}

foreach (string dll in new[] { Built.ExternalEffects, Built.DurableReview, Built.ContosoV5 })
    if (!File.Exists(dll))
    {
        Console.WriteLine($"  FAIL  not built: {dll} (build the solution in this configuration first)");
        Console.WriteLine("\nRESULT 0/1");
        return 1;
    }

TimeSpan pauseTimeout = TimeSpan.FromSeconds(90);
// A worker renews its lease while it lives; a killed one stops, so its lease runs out.
// The harness waits a little longer than the lease before starting the next worker.
const string LeaseSeconds = "6";
TimeSpan leaseGone = TimeSpan.FromSeconds(6.5);
string root = Path.Combine(Path.GetTempPath(), $"restart-tests-{Guid.NewGuid():N}");
const string Claim = "CLM-3001";
try
{
    // ------------------------------------------------------------------
    Console.WriteLine("\n-- 1. External effects: kill the worker inside the payout step");
    string ee = Built.ExternalEffects;
    (string Point, int EffectsAtDeath, string Recovery)[] windows =
    [
        ("before-effect", 0, "not found; sent again"),
        ("after-accept", 1, "by reference"),
        ("after-record", 1, "re-read the operation record"),
    ];
    foreach ((string point, int atDeath, string recovery) in windows)
    {
        string store = Path.Combine(root, "ee-" + point);
        using Worker victim = Worker.Start(ee, "start", Claim, "--store", store, "--pause-at", point, "--lease-seconds", LeaseSeconds);
        bool reached = await victim.WaitForLineAsync($"PAUSED at {point}", pauseTimeout);
        string? op = Read.Operation(victim.Output);
        Exited dead = victim.Kill();
        Exited before = Worker.Run(ee, "status", Claim, "--store", store);
        Thread.Sleep(leaseGone);
        Exited resume = Worker.Run(ee, "resume", Claim, "--store", store);
        Exited after = Worker.Run(ee, "status", Claim, "--store", store);

        Check($"{point}: worker {dead.Pid} reached the point and was killed there (no outcome printed)",
            reached && op is not null && !dead.Output.Contains("Outcome:"));
        Check($"{point}: {atDeath} effect(s) in the provider's journal when it died", Read.Effects(before.Output) == atDeath);
        Check($"{point}: a new process ({resume.Pid}) restores the pre-payout checkpoint and settles: \"{recovery}\"",
            resume.ExitCode == 0 && resume.Pid != dead.Pid && resume.Output.Contains("Restored checkpoint") &&
            resume.Output.Contains(recovery) && resume.Output.Contains("Outcome: Paid"));
        Check($"{point}: exactly one effect, reference contoso:<the approval's operation id>, no new id",
            Read.Effects(after.Output) == 1 && Read.References(after.Output) is [var only] && only == $"contoso:{op}");
    }

    {
        string store = Path.Combine(root, "ee-naive");
        using Worker victim = Worker.Start(ee, "start", Claim, "--store", store, "--pause-at", "after-record", "--lease-seconds", LeaseSeconds);
        await victim.WaitForLineAsync("PAUSED at after-record", pauseTimeout);
        victim.Kill();
        Thread.Sleep(leaseGone);
        Worker.Run(ee, "resume", Claim, "--store", store, "--naive");
        Exited after = Worker.Run(ee, "status", Claim, "--store", store);
        Check("DEFECT REPRODUCED: killed after-record, the naive step (new operation id per attempt) pays a second time",
            Read.Effects(after.Output) == 2 && Read.References(after.Output).Distinct().Count() == 2);
    }

    {
        string store = Path.Combine(root, "ee-unknown");
        Exited first = Worker.Run(ee, "start", Claim, "--store", store, "--fault", "accept-then-timeout");
        Exited between = Worker.Run(ee, "status", Claim, "--store", store);
        Exited second = Worker.Run(ee, "resume", Claim, "--store", store);
        Exited after = Worker.Run(ee, "status", Claim, "--store", store);
        Check("reply lost: the worker ends with an explicit Unknown (exit 6), the journal already holds the payment",
            first.ExitCode == 6 && first.Output.Contains("Outcome: Unknown") && Read.Effects(between.Output) == 1 &&
            between.Output.Contains("payout:     Unknown"));
        Check("a later process reconciles from the restored workflow: found by reference, Paid, still one effect",
            second.ExitCode == 0 && second.Output.Contains("by reference") && Read.Effects(after.Output) == 1);
    }

    {
        string store = Path.Combine(root, "ee-stale");
        Worker.Run(ee, "approve", Claim, "--store", store);
        using Worker a = Worker.Start(ee, "resume", Claim, "--store", store, "--owner", "worker-a", "--lease-seconds", LeaseSeconds, "--no-renew",
            "--pause-at", "before-commit");
        bool paused = await a.WaitForLineAsync("PAUSED at before-commit", pauseTimeout);
        Thread.Sleep(leaseGone);                                     // worker A, frozen (--no-renew), stalls past its lease
        Exited b = Worker.Run(ee, "resume", Claim, "--store", store, "--owner", "worker-b");
        a.Continue();                                                // ... and wakes up
        Exited late = a.WaitForExit();
        Exited after = Worker.Run(ee, "status", Claim, "--store", store);
        Check("worker B takes the expired lease (token 2) while A is stalled, and pays",
            paused && b.ExitCode == 0 && b.Output.Contains("holds token 2") && b.Output.Contains("Outcome: Paid"));
        Check("worker A wakes with token 1: StaleLease (exit 4), nothing committed, one effect",
            late.ExitCode == 4 && late.Output.Contains("StaleLease") && Read.Effects(after.Output) == 1);
    }

    {
        string store = Path.Combine(root, "ee-race");
        Worker.Run(ee, "approve", Claim, "--store", store);
        using Worker x = Worker.Start(ee, "resume", Claim, "--store", store, "--owner", "worker-x");
        using Worker y = Worker.Start(ee, "resume", Claim, "--store", store, "--owner", "worker-y");
        Exited[] both = [x.WaitForExit(), y.WaitForExit()];
        Exited after = Worker.Run(ee, "status", Claim, "--store", store);
        Check("two worker processes started together: one pays, the other finds the lease held or re-reads; one effect",
            both.Count(r => r.Output.Contains("Outcome: Paid: paid")) == 1 &&
            both.Count(r => r.Output.Contains("LEASE HELD") || r.Output.Contains("re-read")) == 1 &&
            Read.Effects(after.Output) == 1);
    }

    {
        // Killed before-effect: the payout began (Paying), the journal is empty. A cancel now does not
        // stop it: the next worker settles the attempt first and, finding nothing, sends the SAME reference.
        string store = Path.Combine(root, "ee-cancel-begun");
        using Worker victim = Worker.Start(ee, "start", Claim, "--store", store, "--pause-at", "before-effect", "--lease-seconds", LeaseSeconds);
        bool reached = await victim.WaitForLineAsync("PAUSED at before-effect", pauseTimeout);
        string? op = Read.Operation(victim.Output);
        victim.Kill();
        Exited cancel = Worker.Run(ee, "cancel", Claim, "--store", store);
        Thread.Sleep(leaseGone);
        Exited resume = Worker.Run(ee, "resume", Claim, "--store", store);
        Exited after = Worker.Run(ee, "status", Claim, "--store", store);
        Check("cancel after the payout began (killed before-effect, 0 effects): the resume settles it, \"not found; sent again\"",
            reached && cancel.Output.Contains("the payout is Paying") && resume.ExitCode == 0 &&
            resume.Output.Contains("not found; sent again") && resume.Output.Contains("too late to stop the payout"));
        Check("... under the approval's reference: Paid, exactly one effect, no new id",
            Read.Effects(after.Output) == 1 && Read.References(after.Output) is [var only] && only == $"contoso:{op}");
    }

    // ------------------------------------------------------------------
    Console.WriteLine("\n-- 2. Durable review: two commands, no shared memory");
    string dr = Built.DurableReview;
    {
        string store = Path.Combine(root, "review");
        using Worker prepare = Worker.Start(dr, "prepare", "--store", store, "--offline");
        Exited prepared = prepare.WaitForExit();
        Exited review = Worker.Run(dr, "review", "CLM-1001", "rita", "approve", "--store", store);
        Exited resume = Worker.Run(dr, "resume", "CLM-1001", "--store", store);
        Check("prepare exited (exit 0) before resume started; resume in a fresh process -> [APPROVED]",
            prepared.ExitCode == 0 && review.ExitCode == 0 && resume.ExitCode == 0 &&
            resume.Output.Contains("[APPROVED] CLM-1001 v1 by rita") && resume.Pid != prepared.Pid);
        foreach (string dll in new[] { Built.DurableReview, Built.ExternalEffects, Built.ContosoV5 })
        {
            IReadOnlyList<string> statics = StaticFields.Mutable(dll);
            Check($"{Path.GetFileName(dll)}: no static field that can be reassigned{(statics.Count == 0 ? "" : ": " + string.Join(", ", statics))}",
                statics.Count == 0);
        }
    }

    // ------------------------------------------------------------------
    Console.WriteLine("\n-- 3. Contoso v5: kill the resume while it pays the approved claim");
    string v5 = Built.ContosoV5;
    (string Point, int EffectsAtDeath, string Recovery)[] contosoWindows =
    [
        ("before-effect", 0, "not found; sent again"),
        ("after-accept", 1, "by reference"),
        ("after-record", 1, "re-read the claim: already Delivered"),
    ];
    foreach ((string point, int atDeath, string recovery) in contosoWindows)
    {
        string store = Path.Combine(root, "v5-" + point);
        Exited prepared = Worker.Run(v5, "prepare", "--store", store);
        Worker.Run(v5, "review", "latest", "rita", "approve", "--store", store);
        using Worker victim = Worker.Start(v5, "resume", "latest", "--store", store, "--pause-at", point, "--lease-seconds", LeaseSeconds);
        bool reached = await victim.WaitForLineAsync($"PAUSED at {point}", pauseTimeout);
        Exited dead = victim.Kill();
        Exited before = Worker.Run(v5, "status", "latest", "--store", store);
        Thread.Sleep(leaseGone);
        Exited resume = Worker.Run(v5, "resume", "latest", "--store", store);
        Exited after = Worker.Run(v5, "status", "latest", "--store", store);
        Check($"v5 {point}: the resume was killed there with {atDeath} payment(s) in the journal",
            prepared.ExitCode == 0 && reached && !dead.Output.Contains("[DELIVERED]") && Read.Effects(before.Output) == atDeath);
        Check($"v5 {point}: a new process reconciles and reports the claim: \"{recovery}\"",
            resume.ExitCode == 0 && resume.Output.Contains(recovery) &&
            (resume.Output.Contains("[DELIVERED]") || resume.Output.Contains("[RECONCILED]")));
        Check($"v5 {point}: one committed operation, one payment",
            Read.Value(after.Output, "operations:") == "1" && Read.Effects(after.Output) == 1);
    }
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
}

Console.WriteLine($"\nRESULT {passed}/{total}");
return passed == total ? 0 : 1;
