using System.Diagnostics;

namespace Ch15.DurableReview;

/// <summary>
/// --self-test: every verb runs as its own child process (dotnet exec of this
/// assembly, --offline) against a fresh temp store, so the only things two
/// steps share are the checkpoint folder and app.db. Prints RESULT n/n.
/// </summary>
internal static class DurableReviewSelfTest
{
    public static async Task<int> RunAsync()
    {
        Console.WriteLine("=== Durable Review: cross-process self-test ===\n");
        Checks c = new();
        string root = Path.Combine(Path.GetTempPath(), $"durable-review-selftest-{Guid.NewGuid():N}");
        try
        {
            Console.WriteLine("-- Graph version (in process)");
            string v1 = GraphVersion.Of(ReviewGraph.Build(new ScriptedChatClient()), ReviewGraph.RulesVersion);
            c.Check("two fresh builds of the same graph have the same version",
                v1 == GraphVersion.Of(ReviewGraph.Build(new ScriptedChatClient()), ReviewGraph.RulesVersion));
            c.Check("an extra step changes the version",
                v1 != GraphVersion.Of(ReviewGraph.Build(new ScriptedChatClient(), next: true), ReviewGraph.RulesVersion));
            c.Check("new rules on the same graph change the version",
                v1 != GraphVersion.Of(ReviewGraph.Build(new ScriptedChatClient(), ReviewGraph.NextRulesVersion),
                    ReviewGraph.NextRulesVersion));

            Console.WriteLine("\n-- prepare, review and resume: three processes");
            Lab lab = new(Path.Combine(root, "happy"));
            Child prepare = lab.Run("prepare");
            c.Check("prepare exits 0 at the review port and finalizes nothing",
                prepare.ExitCode == 0 && prepare.Output.Contains("Review requested for CLM-1001 v1") &&
                !prepare.Output.Contains("[APPROVED]"));
            c.Check("checkpoint files and app.db are on disk",
                Directory.EnumerateFiles(Path.Combine(lab.Store, "checkpoints")).Any() &&
                File.Exists(Path.Combine(lab.Store, "app.db")));
            Child early = lab.Run("resume", "CLM-1001");
            c.Check("resume before any decision waits (exit 5), nothing resumed",
                early.ExitCode == 5 && early.Output.Contains("WAITING") && !early.Output.Contains("re-emitted"));
            Child review = lab.Run("review", "CLM-1001", "rita", "approve");
            c.Check("rita's decision is recorded for v1", review.ExitCode == 0 && review.Output.Contains("for CLM-1001 v1"));
            Child resume = lab.Run("resume", "CLM-1001");
            c.Check("prepare, review and resume ran in three different processes",
                new[] { prepare.Pid, review.Pid, resume.Pid, Environment.ProcessId }.Distinct().Count() == 4);
            c.Check("resume restores the checkpoint prepare wrote and gets the request again",
                resume.Output.Contains("re-emitted in a new process: CLM-1001 v1"));
            int callback = resume.Output.IndexOf("restoring callback: 1 summary written, last CLM-1001 v1", StringComparison.Ordinal);
            int restored = resume.Output.IndexOf("OnCheckpointRestoredAsync", StringComparison.Ordinal);
            c.Check("executor state crossed the process boundary; the callback ran before OnCheckpointRestoredAsync",
                callback >= 0 && restored > callback);
            c.Check("resume -> [APPROVED] CLM-1001 v1 by rita, without calling a model",
                resume.ExitCode == 0 && resume.Output.Contains("[APPROVED] CLM-1001 v1 by rita"));
            Child again = lab.Run("resume", "CLM-1001");
            c.Check("a second resume of a finished claim fails closed", again.ExitCode == 3 && again.Output.Contains("already Approved"));

            Lab rejectLab = new(Path.Combine(root, "reject"));
            rejectLab.Run("prepare");
            rejectLab.Run("review", "CLM-1001", "rita", "reject");
            Child rejected = rejectLab.Run("resume", "CLM-1001");
            c.Check("reject -> [REJECTED], nothing approved",
                rejected.ExitCode == 0 && rejected.Output.Contains("[REJECTED]") && !rejected.Output.Contains("[APPROVED]"));

            Console.WriteLine("\n-- Who may decide");
            Lab who = new(Path.Combine(root, "who"));
            who.Run("prepare", "--requester", "rita");
            c.Check("bob holds no reviewer role: refused",
                who.Run("review", "CLM-1001", "bob", "approve").Output.Contains("REFUSED reviewer_not_authorized"));
            c.Check("rita cannot review her own claim: refused",
                who.Run("review", "CLM-1001", "rita", "approve").Output.Contains("REFUSED separation_of_duties"));
            c.Check("an unknown decision word is not a decision",
                who.Run("review", "CLM-1001", "sam", "approved").ExitCode == 2);
            // Two reviewers decide at the same moment, in two processes: one row wins.
            Child[] race = Lab.Together(who.Start("review", "CLM-1001", "sam", "approve"),
                who.Start("review", "CLM-1001", "rita", "reject"), who.Start("review", "CLM-1001", "sam", "reject"));
            c.Check("three concurrent review processes: exactly one decision stored, the others refused",
                race.Count(r => r.Output.Contains("Decision recorded")) == 1 &&
                race.Count(r => r.Output.Contains("REFUSED")) == 2);

            Console.WriteLine("\n-- Where resume must stop");
            Lab stale = new(Path.Combine(root, "stale"));
            stale.Run("prepare");
            stale.Run("review", "CLM-1001", "rita", "approve");
            stale.Run("amend", "CLM-1001", "99.00");
            Child staleResume = stale.Run("resume", "CLM-1001");
            c.Check("amended after approval -> BLOCKED stale_approval (v1 decision, claim v2)",
                staleResume.ExitCode == 4 && staleResume.Output.Contains("BLOCKED stale_approval") &&
                !staleResume.Output.Contains("re-emitted"));
            Checks.Note(staleResume);
            stale.Run("review", "CLM-1001", "rita", "approve");
            c.Check("a v2 decision against the v1 checkpoint -> BLOCKED stale_checkpoint",
                stale.Run("resume", "CLM-1001").Output.Contains("BLOCKED stale_checkpoint"));
            stale.Run("prepare");
            c.Check("prepare again for v2, then resume -> [APPROVED] v2",
                stale.Run("resume", "CLM-1001").Output.Contains("[APPROVED] CLM-1001 v2 by rita"));

            Lab expired = new(Path.Combine(root, "expired"));
            expired.Run("prepare");
            expired.Run("review", "CLM-1001", "rita", "approve", "--now", "2026-11-20T09:00:00Z");
            Child late = expired.Run("resume", "CLM-1001", "--now", "2026-11-21T10:00:00Z");
            c.Check("resumed 25 hours after the decision -> BLOCKED approval_expired",
                late.ExitCode == 4 && late.Output.Contains("BLOCKED approval_expired"));
            Checks.Note(late);

            Lab revoked = new(Path.Combine(root, "revoked"));
            revoked.Run("prepare");
            revoked.Run("review", "CLM-1001", "rita", "approve");
            revoked.Run("revoke", "rita");
            Child gone = revoked.Run("resume", "CLM-1001");
            c.Check("reviewer's role revoked before resume -> BLOCKED reviewer_revoked",
                gone.ExitCode == 4 && gone.Output.Contains("BLOCKED reviewer_revoked") && !gone.Output.Contains("[APPROVED]"));
            Checks.Note(gone);

            Console.WriteLine("\n-- A new build meets a pending review");
            Lab upgrade = new(Path.Combine(root, "upgrade"));
            upgrade.Run("prepare");
            upgrade.Run("review", "CLM-1001", "rita", "approve");
            Child mismatch = upgrade.Run("resume", "CLM-1001", "--graph", "next");
            c.Check("upgraded graph -> BLOCKED graph_version_mismatch before the checkpoint is touched",
                mismatch.ExitCode == 4 && mismatch.Output.Contains("BLOCKED graph_version_mismatch"));
            Checks.Note(mismatch);
            Child mafOnly = upgrade.Run("resume", "CLM-1001", "--graph", "next", "--skip-version-check");
            c.Check("without the check, MAF itself refuses the changed shape (InvalidDataException)",
                mafOnly.ExitCode == 4 && mafOnly.Output.Contains("BLOCKED checkpoint_incompatible") &&
                mafOnly.Output.Contains("not compatible with the workflow"));
            Checks.Note(mafOnly);
            Child rulesBlocked = upgrade.Run("resume", "CLM-1001", "--rules", "next");
            c.Check("same graph, new rules -> BLOCKED graph_version_mismatch",
                rulesBlocked.Output.Contains("BLOCKED graph_version_mismatch"));
            Child rulesSkipped = upgrade.Run("resume", "CLM-1001", "--rules", "next", "--skip-version-check");
            c.Defect("without the check, MAF resumes a v1-rules approval under review-rules-2",
                rulesSkipped.ExitCode == 0 && rulesSkipped.Output.Contains("rules review-rules-2"));

            Console.WriteLine("\n-- Two resumes at once, and a damaged store");
            Lab twice = new(Path.Combine(root, "twice"));
            twice.Run("prepare");
            twice.Run("review", "CLM-1001", "rita", "approve");
            Child[] both = Lab.Together(twice.Start("resume", "CLM-1001"), twice.Start("resume", "CLM-1001"));
            c.Check("two concurrent resume processes: exactly one [APPROVED], the other fails",
                both.Count(r => r.Output.Contains("[APPROVED]")) == 1 && both.Count(r => r.ExitCode != 0) == 1);
            if (both.FirstOrDefault(r => r.ExitCode != 0) is { } loser) Checks.Note(loser);

            Lab damaged = new(Path.Combine(root, "damaged"));
            damaged.Run("prepare");
            damaged.Run("review", "CLM-1001", "rita", "approve");
            foreach (string file in Directory.GetFiles(Path.Combine(damaged.Store, "checkpoints"))
                         .Where(f => !f.EndsWith("index.jsonl", StringComparison.Ordinal)))
                File.Delete(file);
            Child broken = damaged.Run("resume", "CLM-1001");
            c.Check("deleted checkpoint file -> FAILED (exit 3), nothing approved",
                broken.ExitCode == 3 && broken.Output.Contains("FAILED") && !broken.Output.Contains("[APPROVED]"));
            Checks.Note(broken);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        await Task.CompletedTask;
        return c.Finish();
    }
}

/// <summary>PASS/FAIL lines and the RESULT line, kept in an instance (no static state).</summary>
internal sealed class Checks
{
    private int _passed, _total;

    public void Check(string name, bool ok)
    {
        _total++;
        if (ok) _passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    }

    /// <summary>Prints the child's verdict line (BLOCKED, FAILED, REFUSED or WAITING), indented.</summary>
    public static void Note(Child child) =>
        Console.WriteLine("        " + (child.Output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l =>
            l.StartsWith("BLOCKED", StringComparison.Ordinal) || l.StartsWith("FAILED", StringComparison.Ordinal) ||
            l.StartsWith("REFUSED", StringComparison.Ordinal) || l.StartsWith("WAITING", StringComparison.Ordinal)) ?? "(no verdict line)"));

    /// <summary>Passes when the broken behaviour really happens.</summary>
    public void Defect(string name, bool reproduced) => Check($"DEFECT REPRODUCED: {name}", reproduced);

    public int Finish()
    {
        Console.WriteLine($"\nRESULT {_passed}/{_total}");
        return _passed == _total ? 0 : 1;
    }
}

internal sealed record Child(int ExitCode, string Output, int Pid);

/// <summary>One temp store and the child processes that share it.</summary>
internal sealed class Lab(string store)
{
    public string Store { get; } = store;

    public Child Run(params string[] args) => Wait(Start(args));

    /// <summary>Starts a child process (dotnet exec of this assembly) without waiting for it.</summary>
    public Process Start(params string[] args)
    {
        ProcessStartInfo psi = new(DotnetHost())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(typeof(Lab).Assembly.Location);
        foreach (string arg in args) psi.ArgumentList.Add(arg);
        psi.ArgumentList.Add("--store");
        psi.ArgumentList.Add(Store);
        psi.ArgumentList.Add("--offline");
        return Process.Start(psi)!;
    }

    /// <summary>Waits for processes that were all started before the first wait: they really overlap.</summary>
    public static Child[] Together(params Process[] processes) => [.. processes.Select(Wait)];

    private static Child Wait(Process process)
    {
        using (process)
        {
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(120)))
            {
                process.Kill(entireProcessTree: true);
                return new Child(-1, "timed out", process.Id);
            }
            return new Child(process.ExitCode, stdout.Result + stderr.Result, process.Id);
        }
    }

    private static string DotnetHost()
    {
        string? current = Environment.ProcessPath;
        if (current is not null && Path.GetFileNameWithoutExtension(current).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return current;
        string? host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        return !string.IsNullOrEmpty(host) && File.Exists(host) ? host : "dotnet";
    }
}
