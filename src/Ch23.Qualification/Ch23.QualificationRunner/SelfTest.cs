using System.Text.Json;
using Ch16.SkillLifecycle;

namespace Ch23.QualificationRunner;

/// <summary>
/// --self-test: the runner's rules on canned runs and temp folders. No child
/// process, no model. Each "Where it fails" case is reproduced first (DEFECT
/// REPRODUCED: the naive rule gets it wrong), then the runner's rule is checked.
/// </summary>
internal static class RunnerSelfTest
{
    private static int _passed, _total;

    public static async Task<int> RunAsync()
    {
        Console.WriteLine("=== Qualification runner (self-test, offline) ===\n");
        string root = Path.Combine(Path.GetTempPath(), $"ch23-selftest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            Counting();
            ZeroCases();
            await SkillDriftAsync(root);
            FakeEvidence(root);
            await IdentityAsync(root);
            await BudgetAndLockAsync(root);
            DossierRules();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        Console.WriteLine($"\nRESULT {_passed}/{_total}");
        return _passed == _total ? 0 : 1;
    }

    private static StepSpec Run(string id, string provider = "fake", CountRule? count = null) =>
        new() { Id = id, Kind = "run", Project = "p.csproj", Provider = provider, Count = count ?? CountRule.Result };

    private static void Counting()
    {
        Console.WriteLine("-- 1. Counting cases");
        Check("result rule: two RESULT lines (11/11 and 11/11) count 22/22",
            CountRule.Result.Apply("RESULT 11/11\n...\nRESULT 11/11\n") == (22, 22));
        Check("result rule: 'RESULT 3/3 (partial)' and an indented RESULT are not counted",
            CountRule.Result.Apply("RESULT 3/3 (partial)\n  RESULT 2/2\n") == (0, 0));
        CountRule tally = new("tally", Pass: @"^\s+as expected", Fail: @"^\s+differs");
        Check("tally rule: 2 'as expected' and 1 'differs' lines count 2/3",
            tally.Apply("  as expected  a\n  differs  b\n  as expected  c\n") == (2, 3));
        CountRule ratio = new("ratio", @"Passed (?<pass>\d+)/(?<total>\d+)");
        Check("ratio rule: 'Items: 21 evaluated, Passed 9/21' counts 9/21", ratio.Apply("Items: 21 evaluated, Passed 9/21") == (9, 21));
    }

    private static void ZeroCases()
    {
        Console.WriteLine("\n-- 2. A green command that selected zero cases");
        RawRun empty = new(0, "=== Contoso ===\nRESULT 0/0", 1.2);
        bool naiveGreen = empty.ExitCode == 0;
        Check("DEFECT REPRODUCED: an exit-code-only gate calls 'RESULT 0/0, exit 0' green", naiveGreen);
        var zero = Campaign.Judge(Run("selftest"), empty);
        Check($"rule: refused ({zero.Reason})", zero.Status == StepStatus.Refused && zero.Total == 0);
        var silent = Campaign.Judge(Run("selftest"), new RawRun(0, "Usage: dotnet run -- --self-test", 0.4));
        Check($"no RESULT line at all (a mistyped flag printed usage), exit 0: {silent.Status}", silent.Status == StepStatus.Refused);
        var partial = Campaign.Judge(Run("selftest"), new RawRun(0, "RESULT 5/6", 1));
        var crashed = Campaign.Judge(Run("selftest"), new RawRun(1, "RESULT 6/6", 1));
        Check($"exit 0 with RESULT 5/6: {partial.Status} ({partial.Reason}); exit 1 with 6/6: {crashed.Status}",
            partial.Status == StepStatus.Failed && crashed.Status == StepStatus.Failed);
        StepSpec build = new() { Id = "build", Kind = "build", Project = "p.csproj" };
        var noSummary = Campaign.Judge(build, new RawRun(0, "Restore complete.", 2));
        var warned = Campaign.Judge(build, new RawRun(0, "Build succeeded.\n    2 Warning(s)\n    0 Error(s)", 9));
        var clean = Campaign.Judge(build, new RawRun(0, "Build succeeded.\n    0 Warning(s)\n    0 Error(s)", 9));
        Check($"build: no summary {noSummary.Status}, 2 warnings {warned.Status}, clean {clean.Status}",
            noSummary.Status == StepStatus.Refused && warned.Status == StepStatus.Failed && clean.Status == StepStatus.Passed);
        var evalFailed = Campaign.Judge(Run("eval", "ollama", new("ratio", @"Passed (?<pass>\d+)/(?<total>\d+)")) with { CompletedExitCodes = [0, 1] },
            new RawRun(1, "Passed 9/21", 261));
        var evalCrashed = Campaign.Judge(Run("eval", "ollama", new("ratio", @"Passed (?<pass>\d+)/(?<total>\d+)")) with { CompletedExitCodes = [0, 1] },
            new RawRun(3, "Passed 0/21", 4));
        Check($"provider step, exit 1 with Passed 9/21: {evalFailed.Status} 9/21 (the matrix judges it); exit 3: {evalCrashed.Status}",
            evalFailed.Status == StepStatus.Completed && evalFailed.Passed == 9 && evalCrashed.Status == StepStatus.Failed);
    }

    private static async Task SkillDriftAsync(string root)
    {
        Console.WriteLine("\n-- 3. A skill folder changed in place after qualification");
        string skills = Path.Combine(root, "skills"), dir = Path.Combine(skills, "refund-escalation");
        Directory.CreateDirectory(Path.Combine(dir, "references"));
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), "---\nname: refund-escalation\ndescription: Decide whether a refund goes to a manager.\n" +
            "metadata:\n  owner: policy-desk\n  version: \"1.0.0\"\n---\n\n# Refund escalation\n\n1. Read `references/thresholds.md`.\n");
        string thresholds = Path.Combine(dir, "references", "thresholds.md");
        File.WriteAllText(thresholds, "Refunds over 500.00 go to a manager.\n");
        var skill = (await SkillDiscovery.FromFolderAsync(skills)).Single();
        string sha = await SkillRegistry.HashAsync(skill);
        string lockPath = Path.Combine(root, "skills.lock.json");
        File.WriteAllText(lockPath, JsonSerializer.Serialize(new[] { new { Name = "refund-escalation", Version = "1.0.0", Sha256 = sha } }));
        List<string> p0 = [];
        var frozen = await CandidateIdentity.HashSkillsAsync(skills, lockPath, p0);
        Check($"frozen: refund-escalation 1.0.0 {sha[..12]} matches its lock", p0.Count == 0 && frozen.Single().Match);

        File.WriteAllText(thresholds, "Refunds over 5000.00 go to a manager.\n");     // edited in place, front matter untouched
        var after = (await SkillDiscovery.FromFolderAsync(skills)).Single();
        bool naiveSame = after.Frontmatter.Name == skill.Frontmatter.Name
            && after.Frontmatter.Metadata?.GetValueOrDefault("version")?.ToString() == "1.0.0";
        Check("DEFECT REPRODUCED: name and version unchanged, so a name@version check calls it the qualified skill", naiveSame);
        List<string> p1 = [];
        var drifted = await CandidateIdentity.HashSkillsAsync(skills, lockPath, p1);
        Check($"rule: content hash {drifted.Single().Sha256[..12]} differs from the lock, the candidate is refused",
            !drifted.Single().Match && p1.Count == 1 && p1[0].Contains("differs from its lock", StringComparison.Ordinal));
    }

    private static void FakeEvidence(string root)
    {
        Console.WriteLine("\n-- 4. A fake-client pass reported as model quality");
        string logs = Path.Combine(root, "out", "logs");
        Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, "evidence-selftest.log"), "  PASS  supported answer cites the chunk\nRESULT 14/14\n");
        File.WriteAllText(Path.Combine(logs, "evidence-live.log"), "  evidence.support  0/9\n");
        File.WriteAllText(Path.Combine(logs, "v6.log"), "  PASS  bob (same tenant, not his claim): 404\n  FAIL  no token: 401\n");
        Dictionary<string, StepResult> steps = new()
        {
            ["evidence-selftest"] = new("evidence-selftest", "run", "e.csproj", "", "fake", 0, 14, 14, 1, StepStatus.Passed, null, "logs/evidence-selftest.log"),
            ["evidence-live"] = new("evidence-live", "run", "e.csproj", "", "ollama", 1, 9, 21, 261, StepStatus.Completed, null, "logs/evidence-live.log"),
            ["v6"] = new("v6", "run", "v6.csproj", "", "fake", 1, 24, 25, 20, StepStatus.Failed, null, "logs/v6.log"),
        };
        string outDir = Path.Combine(root, "out");
        MatrixRow quality = new() { Id = "Q1", Promise = "answers are supported by the cited evidence", Evidence = "provider",
            Step = "evidence-selftest", Data = "evidence-cases.v1", Expected = "support on every answer" };
        bool naive = steps[quality.Step!].Status == StepStatus.Passed;
        Check("DEFECT REPRODUCED: 'the step passed' marks the quality promise passed from a fake-model self-test", naive);
        RowResult refused = Matrix.Evaluate(quality, steps, outDir);
        Check($"rule: {refused.Status} ({refused.Observed})", refused.Status == Matrix.Refused);
        RowResult real = Matrix.Evaluate(quality with { Step = "evidence-live", Ratio = @"evidence\.support\s+(?<pass>\d+)/(?<total>\d+)" }, steps, outDir);
        Check($"the same promise on the live step, threshold 1.0: {real.Status} {real.Observed}", real.Status == Matrix.Failed && real.Observed == "0/9");
        RowResult pass = Matrix.Evaluate(new MatrixRow { Id = "I1", Promise = "a valid but unauthorized claim id is not found", Evidence = "integration",
            Step = "v6", Check = "bob (same tenant, not his claim): 404", Data = "v5 store", Expected = "404" }, steps, outDir);
        RowResult fail = Matrix.Evaluate(new MatrixRow { Id = "I2", Promise = "no token is 401", Evidence = "integration",
            Step = "v6", Check = "no token: 401", Data = "-", Expected = "401" }, steps, outDir);
        RowResult missing = Matrix.Evaluate(new MatrixRow { Id = "I3", Promise = "x", Evidence = "integration",
            Step = "v6", Check = "a check nobody wrote", Data = "-", Expected = "-" }, steps, outDir);
        Check($"a PASS line: {pass.Status}; a FAIL line: {fail.Status}; a check that is not in the log: {missing.Status}",
            pass.Status == Matrix.Passed && fail.Status == Matrix.Failed && missing.Status == Matrix.Refused);
        RowResult human = Matrix.Evaluate(new MatrixRow { Id = "H1", Promise = "ops accepts", Evidence = "human", Step = "v6", Data = "-", Expected = "-" }, steps, outDir);
        RowResult untested = Matrix.Evaluate(new MatrixRow { Id = "U1", Promise = "fast", Evidence = "workload", Data = "-", Expected = "-" }, steps, outDir);
        Check($"a human row: {human.Status}, whatever the step did; a row with no test: {untested.Status}",
            human.Status == Matrix.PendingHuman && untested.Status == Matrix.Refused);
    }

    private static async Task IdentityAsync(string root)
    {
        Console.WriteLine("\n-- 5. The manifest and the candidate identity");
        Check("secret scan: a clean profile has no finding",
            SecretScan.Find("""{"profile":{"provider":"ollama","endpoint":"http://localhost:11434","apiKey":""}}""").Count == 0);
        List<string> found = SecretScan.Find("""{"profile":{"apiKey":"abc123","endpoint":"https://x.example/openai?api-key=f00"}}""");
        Check($"secret scan: a key under 'apiKey' and a key in a URL ({found.Count} findings)", found.Count == 2);

        string repo = Path.Combine(root, "repo"), proj = Path.Combine(repo, "app");
        Directory.CreateDirectory(Path.Combine(repo, "data"));
        Directory.CreateDirectory(proj);
        File.WriteAllText(Path.Combine(repo, "global.json"), """{"sdk":{"version":"10.0.100","rollForward":"latestMajor"}}""");
        File.WriteAllText(Path.Combine(repo, "Directory.Packages.props"),
            """<Project><ItemGroup><PackageVersion Include="Lib.A" Version="1.2.0" /><PackageVersion Include="Lib.B" Version="2.0.0-preview.1" /></ItemGroup></Project>""");
        File.WriteAllText(Path.Combine(proj, "app.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        string lockFile = Path.Combine(proj, "packages.lock.json");
        void Lock(string a) => File.WriteAllText(lockFile,
            """{"version":2,"dependencies":{"net10.0":{"Lib.A":{"type":"Direct","resolved":"A"},"Lib.B":{"type":"Direct","resolved":"2.0.0-preview.1"},"Lib.C":{"type":"Transitive","resolved":"9.0.0"}}}}"""
                .Replace("\"A\"", $"\"{a}\"", StringComparison.Ordinal));
        Lock("1.2.0");
        File.WriteAllText(Path.Combine(repo, "data", "faq.md"), "PTO: 15 days.\r\n");
        string manifestPath = Path.Combine(repo, "candidate.json");
        CandidateManifest m = new()
        {
            Candidate = "test", Profile = new("ollama", "llama3.2", null, "http://localhost:11434", "release"),
            Identity = new IdentityInputs { Projects = ["app/app.csproj"], Data = [new("faq", "data")] }, Layers = [], Matrix = [],
        };
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(m, CandidateManifest.Json));
        CandidateIdentity a = await CandidateIdentity.ComputeAsync(repo, manifestPath, m, promptsBuilt: false);
        Check($"identity: SDK {a.GlobalJsonSdk}, {a.Packages.Count} direct packages, prerelease {string.Join(", ", CandidateIdentity.PrereleaseIds(a))}, no problem",
            a.Problems.Count == 0 && a.Packages.Count == 2 && CandidateIdentity.PrereleaseIds(a).Single() == "Lib.B 2.0.0-preview.1");
        File.WriteAllText(Path.Combine(repo, "data", "faq.md"), "PTO: 15 days.\n");
        CandidateIdentity lf = await CandidateIdentity.ComputeAsync(repo, manifestPath, m, promptsBuilt: false);
        Check("a CRLF to LF checkout of the same data keeps the identity", lf.Sha256 == a.Sha256);
        Lock("1.3.0");
        CandidateIdentity b = await CandidateIdentity.ComputeAsync(repo, manifestPath, m, promptsBuilt: false);
        Check($"a lock file that resolves Lib.A 1.3.0 against a 1.2.0 pin: refused, identity {b.Sha256[..8]} != {a.Sha256[..8]}",
            b.Problems.Any(p => p.Contains("Directory.Packages.props pins 1.2.0", StringComparison.Ordinal)) && b.Sha256 != a.Sha256);
        Lock("1.2.0");
        File.Delete(Path.Combine(repo, "data", "faq.md"));
        CandidateIdentity c = await CandidateIdentity.ComputeAsync(repo, manifestPath, m, promptsBuilt: false);
        Check("a data version with no file: refused", c.Problems.Any(p => p.StartsWith("data faq", StringComparison.Ordinal)));
    }

    private static async Task BudgetAndLockAsync(string root)
    {
        Console.WriteLine("\n-- 6. Budget, configuration and the model lock");
        // A canned executor: each step "takes" 40 s; one given less than that is stopped at its limit.
        StepExecutor canned = (step, _, timeout) => Task.FromResult(timeout.TotalSeconds < 40
            ? new RawRun(-1, "", timeout.TotalSeconds, $"the {timeout.TotalSeconds:F0} s limit")
            : new RawRun(0, "RESULT 3/3", 40));
        Campaign c = new(Path.Combine(root, "budget", "logs"), canned) { Budget = TimeSpan.FromMinutes(1) };
        StepResult s1 = await c.RunStepAsync(Run("live-1", "ollama")), s2 = await c.RunStepAsync(Run("live-2", "ollama")),
            s3 = await c.RunStepAsync(Run("live-3", "ollama"));
        Check($"1 min budget, 40 s steps: {s1.Status}, {s2.Status} ({s2.Reason}), {s3.Status} ({s3.Reason})",
            s1.Status == StepStatus.Completed && s2.Status == StepStatus.Stopped && s3.Status == StepStatus.NotRun);
        StepResult cloud = await c.RunStepAsync(Run("azure", "azure") with { RequiresEnv = ["CH23_SELFTEST_UNSET_ENDPOINT"] });
        Check($"a cloud step with no endpoint configured: {cloud.Status} ({cloud.Reason}), 0 cases", cloud.Status == StepStatus.NotRun && cloud.Total == 0);

        string lockPath = Path.Combine(root, "ollama.lock");
        const int racers = 8;
        using Barrier start = new(racers);
        ModelLock?[] got = await Task.WhenAll(Enumerable.Range(0, racers).Select(_ => Task.Run(() =>
        {
            start.SignalAndWait();
            return ModelLock.TryAcquire(lockPath);
        })));
        int holders = got.Count(g => g is not null);
        foreach (ModelLock? g in got) g?.Dispose();
        ModelLock? again = ModelLock.TryAcquire(lockPath);
        Check($"{racers} runs race for the model lock (Task.Run, Barrier): {holders} holds it; released, it can be taken again",
            holders == 1 && again is not null);
        again?.Dispose();
        Check("released: the lock file is gone", !File.Exists(lockPath));
    }

    private static void DossierRules()
    {
        Console.WriteLine("\n-- 7. The dossier");
        CandidateIdentity id = new()
        {
            Candidate = "test", ManifestSha256 = new string('a', 64), GlobalJsonSdk = "10.0.100", Packages = [], LockFiles = [], Prompts = [],
            Skills = [], Data = [], Profile = new("ollama", "llama3.2", null, "http://localhost:11434", "release"), Problems = [],
        };
        List<RowResult> rows = [new("Q1", "supported answers", "provider", "live", "cases", "1.0", Matrix.Failed, "0/9", "logs/live.log")];
        QualificationResults r = new()
        {
            Modes = ["offline", "real"], StartedUtc = "s", FinishedUtc = "f", Platform = "p", SdkUsed = "11", CandidateSha256 = id.Sha256,
            Candidate = id, IdentityUnchangedAfterRun = true, Layers = [], Matrix = rows, Totals = Matrix.Count([], rows), Budgets = [],
        };
        CapabilityFile caps = new("release", [new("claims.decide", ["POST /claims/{id}/approval"], false, "effects are simulated")]);
        DecisionRecord decision = new("not taken", "pending", []);
        bool refusedUnjournaled = Throws(() => Dossier.Render(r, new FailureJournal([], decision), caps, []));
        bool refusedNoReason = Throws(() => Dossier.Render(r, new FailureJournal([new("F1", "real", "Q1", "no support", "known-limitation", "small model")], decision),
            new CapabilityFile("release", [new("claims.decide", [], false, "")]), []));
        string md = Dossier.Render(r, new FailureJournal([new("F1", "real", "Q1", "no support", "known-limitation", "small model")], decision), caps, ["one limit"]);
        Check("a failed row with no journal entry, or a capability disabled with no reason: the dossier is refused", refusedUnjournaled && refusedNoReason);
        Check("rendered: the journal entry shows what the run observed (failed 0/9), the disabled capability its reason",
            md.Contains("| F1 | real | Q1 | failed 0/9 |", StringComparison.Ordinal) && md.Contains("DISABLED, effects are simulated", StringComparison.Ordinal));
    }

    private static bool Throws(Action a)
    {
        try { a(); return false; }
        catch (InvalidDataException) { return true; }
    }

    private static void Check(string name, bool ok)
    {
        _total++;
        if (ok) _passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    }
}
