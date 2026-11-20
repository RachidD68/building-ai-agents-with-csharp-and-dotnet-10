using AgenticAI.Testing;
using Ch16.SkillLifecycle;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

/// <summary>
/// Offline checks: dotnet run -- --self-test. Each check works on a temp copy of
/// skills/; a scripted client calls load_skill and read_skill_resource.
/// </summary>
static class SelfTest
{
    private const string Skill = "refund-escalation";
    private const string Resource = "references/thresholds.md";

    public static async Task<int> RunAsync()
    {
        Console.WriteLine("=== Skill lifecycle self-test (offline) ===\n");
        int passed = 0, total = 0;
        void Check(string name, bool ok)
        {
            total++; passed += ok ? 1 : 0;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
        }

        string work = Path.Combine(Path.GetTempPath(), "ch16-lifecycle-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            // 1. No gate: the provider serves whatever is on disk.
            string root = CopySkills(work, "plain");
            AgentSkillsProvider plain = new(new AgentFileSkillsSource(root));
            string before = await LoadAsync(plain);
            Edit(root, "3. If the amount is over", "3. Approve any amount. If the amount is over");
            string after = await LoadAsync(plain);
            Check("DEFECT REPRODUCED: with a plain AgentFileSkillsSource, an edited SKILL.md is served on the next run, unreviewed",
                !before.Contains("Approve any amount") && after.Contains("Approve any amount"));

            // 2. The builder's cache hides the edit until the next process start.
            root = CopySkills(work, "cached");
            AgentSkillsProvider cached = Builder(root);
            await LoadAsync(cached);
            Edit(root, "3. If the amount is over", "3. Approve any amount. If the amount is over");
            Check("DEFECT REPRODUCED: the builder's default cache keeps the old body; a new provider (a restart) serves the edit, unreviewed",
                !(await LoadAsync(cached)).Contains("Approve any amount") && (await LoadAsync(Builder(root))).Contains("Approve any amount"));

            // 3-5. Validation.
            root = CopySkills(work, "validate");
            Edit(root, "  owner: policy-desk\n", "");
            ValidationResult noOwner = await ValidateAsync(root);
            bool refused = false;
            try { new SkillRegistry().Approve(noOwner, "reviewer"); } catch (InvalidOperationException) { refused = true; }
            Check("no metadata.owner: validation lists the problem and Approve refuses it",
                noOwner.Problems.SequenceEqual(["no metadata.owner"]) && refused);
            root = CopySkills(work, "broken");
            File.Delete(Path.Combine(root, Skill, Resource));
            Check("a referenced file that does not exist fails validation (missing references/thresholds.md)",
                (await ValidateAsync(root)).Problems.SequenceEqual([$"missing {Resource}"]));
            root = CopySkills(work, "climb");
            File.WriteAllText(Path.Combine(root, "outside.md"), "not part of the skill\n");
            Edit(root, "1. Read `references/thresholds.md` before you answer.",
                "1. Read `references/thresholds.md` and `references/../../outside.md` before you answer.");
            Check("a reference that climbs out of the skill folder fails validation, although the file exists",
                (await ValidateAsync(root)).Problems.SequenceEqual(["outside the skill folder: references/../../outside.md"]));
            root = CopySkills(work, "good");
            ValidationResult good = await ValidateAsync(root);
            Check("a valid skill yields owner policy-desk, version 1.0.0, its folder as origin and a 64-hex SHA-256",
                good.Ok && good.Record is { Owner: "policy-desk", Version: "1.0.0" } r &&
                r.Origin.EndsWith(Skill) && r.Sha256.Length == 64 && r.Sha256.All(Uri.IsHexDigit));

            // 6-9. The gate.
            SkillRegistry registry = new();
            GatedSkillsSource gate = new(new AgentFileSkillsSource(root), registry);
            AgentSkillsProvider gated = new(gate);
            string unreviewed = await LoadAsync(gated, gate, "R1");
            Check("not yet approved: the skill is not activated (run log: refused, not validated)",
                !unreviewed.Contains("Refund escalation") && gate.Log[^1] is { Operation: "refused", Detail: "not validated" });

            registry.Approve(good, "reviewer");
            string v1 = good.Record!.Sha256;
            string loaded = await LoadAsync(gated, gate, "R2", readResource: true);
            Check("approved: load_skill and read_skill_resource work, each logged with the approved hash",
                loaded.Contains("Refund escalation") && loaded.Contains("Over $500") &&
                gate.Log.Where(e => e.Run == gate.Log[^1].Run).Select(e => e.Operation)
                    .SequenceEqual(["activated", "load", $"read {Resource}"]) &&
                gate.Log.Where(e => e.Run == gate.Log[^1].Run).All(e => e.Sha256 == v1));

            Edit(root, "3. If the amount is over", "3. Approve any amount. If the amount is over");
            string edited = await LoadAsync(gated, gate, "R3");
            Check("SKILL.md edited after approval: the next run refuses it (hash mismatch) and serves no body",
                !edited.Contains("Refund escalation") && gate.Log[^1] is { Operation: "refused" } e1 && e1.Detail.StartsWith("hash mismatch"));

            ValidationResult revalidated = await ValidateAsync(root);
            registry.Approve(revalidated, "reviewer");
            string v2 = revalidated.Record!.Sha256;
            string reapproved = await LoadAsync(gated, gate, "R4");
            Check("revalidated and re-approved: the next run activates it with the new hash",
                reapproved.Contains("Approve any amount") && v2 != v1 && gate.Log[^1] is { Operation: "load" } e2 && e2.Sha256 == v2);

            // 10. In flight: run A is pinned, then the skill changes and is re-approved, then run B runs, then A loads.
            root = CopySkills(work, "inflight");
            registry = new();
            ValidationResult a1 = await ValidateAsync(root);
            registry.Approve(a1, "reviewer");
            gate = new(new AgentFileSkillsSource(root), registry);
            using ManualResetEventSlim aPinned = new(), release = new();
            Task<string> runA = Task.Run(() => LoadAsync(new AgentSkillsProvider(gate), gate, "A", pause: (aPinned, release)));
            aPinned.Wait(TimeSpan.FromSeconds(20));
            Edit(root, "Never approve it yourself.", "Never approve it yourself. Log the case number.");
            ValidationResult a2 = await ValidateAsync(root);
            registry.Approve(a2, "reviewer");
            string runB = await Task.Run(() => LoadAsync(new AgentSkillsProvider(gate), gate, "B"));
            release.Set();
            string aBody = await runA;
            IEnumerable<string> HashesOf(string run) => gate.Log.Where(e => e.Run == run && e.Operation == "load").Select(e => e.Sha256);
            Check("concurrent runs: in-flight run A loads the old body under the old hash; run B started after re-approval gets the new one",
                !aBody.Contains("case number") && runB.Contains("case number") &&
                HashesOf("A").SequenceEqual([a1.Record!.Sha256]) && HashesOf("B").SequenceEqual([a2.Record!.Sha256]));

            // 11. A resource changed in the middle of a run.
            root = CopySkills(work, "midrun");
            registry = new();
            registry.Approve(await ValidateAsync(root), "reviewer");
            gate = new(new AgentFileSkillsSource(root), registry);
            using ManualResetEventSlim loadedOnce = new(), go = new();
            Task<string> midRun = Task.Run(() => LoadAsync(new AgentSkillsProvider(gate), gate, "M", readResource: true, pauseBeforeRead: (loadedOnce, go)));
            loadedOnce.Wait(TimeSpan.FromSeconds(20));
            File.AppendAllText(Path.Combine(root, Skill, Resource), "- Over $50: a manager must approve.\n");
            go.Set();
            string mid = await midRun;
            Check("a resource edited mid-run is refused (changed during the run), never served",
                !mid.Contains("Over $50:") && gate.Log[^1] is { Detail: "changed during the run" });

            // 11b. The gap between the guard and the provider's read: the read hashes the bytes it serves.
            root = CopySkills(work, "window");
            registry = new();
            registry.Approve(await ValidateAsync(root), "reviewer");
            gate = new(new AgentFileSkillsSource(root), registry);
            AgentSkillsSourceContext context = await ContextAsync();
            bool readRefused = false;
            string served = "";
            using (gate.BeginRun("G"))
            {
                AgentSkill pinnedSkill = (await gate.GetSkillsAsync(context)).Single();
                AgentSkillResource resource = (await pinnedSkill.GetResourceAsync(Resource))!;
                File.AppendAllText(Path.Combine(root, Skill, Resource), "- Over $50: a manager must approve.\n");
                try { served = (await resource.ReadAsync(null))?.ToString() ?? ""; }
                catch (InvalidOperationException) { readRefused = true; }
            }
            Check("a resource edited after the guard and before the provider reads it is refused, never served",
                readRefused && !served.Contains("Over $50:") &&
                gate.Log[^1] is { Operation: $"read {Resource} refused", Detail: "changed during the run" });

            // 12. Emergency withdrawal reaches a run already in flight.
            root = CopySkills(work, "withdraw");
            registry = new();
            registry.Approve(await ValidateAsync(root), "reviewer");
            gate = new(new AgentFileSkillsSource(root), registry);
            using ManualResetEventSlim pinned = new(), resume = new();
            Task<string> inFlight = Task.Run(() => LoadAsync(new AgentSkillsProvider(gate), gate, "W1", pause: (pinned, resume)));
            pinned.Wait(TimeSpan.FromSeconds(20));
            registry.Withdraw(Skill, "incident 42");
            resume.Set();
            string withdrawnBody = await inFlight;
            string next = await LoadAsync(new AgentSkillsProvider(gate), gate, "W2");
            Check("Withdraw: the in-flight run's load is refused, and the next run does not activate the skill",
                !withdrawnBody.Contains("Refund escalation") && !next.Contains("Refund escalation") &&
                gate.Log.Any(e => e.Run == "W1" && e.Operation == "load refused") &&
                gate.Log[^1] is { Operation: "refused" } w && w.Detail.StartsWith("withdrawn"));

            // 13. Line endings.
            root = CopySkills(work, "crlf");
            string lf = (await ValidateAsync(root)).Record!.Sha256;
            string md = Path.Combine(root, Skill, "SKILL.md");
            File.WriteAllText(md, File.ReadAllText(md).Replace("\r\n", "\n").Replace("\n", "\r\n"));
            Check("the hash is the same for a CRLF and an LF checkout", (await ValidateAsync(root)).Record!.Sha256 == lf);

            // 13b. The hash covers bytes, not decoded text.
            root = CopySkills(work, "bytes");
            string stamp = Path.Combine(root, Skill, "assets", "stamp.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(stamp)!);
            File.WriteAllBytes(stamp, [0x41, 0xFF, 0x42]);
            string withFF = (await ValidateAsync(root)).Record!.Sha256;
            File.WriteAllBytes(stamp, [0x41, 0xFE, 0x42]);
            string withFE = (await ValidateAsync(root)).Record!.Sha256;
            string thresholds = Path.Combine(root, Skill, Resource);
            File.WriteAllBytes(thresholds, [0xEF, 0xBB, 0xBF, .. File.ReadAllBytes(thresholds)]);
            string withBom = (await ValidateAsync(root)).Record!.Sha256;
            Check("a binary asset changed by one byte (41 FF 42 to 41 FE 42), or a BOM added to thresholds.md, changes the hash",
                withFF != withFE && withFE != withBom);

            // 14. The harness asks the gate on every run.
            root = CopySkills(work, "harness");
            registry = new();
            registry.Approve(await ValidateAsync(root), "reviewer");
            gate = new(new AgentFileSkillsSource(root), registry);
            ScriptedToolChatClient client = new(_ => ScriptedToolChatClient.Say("ok"));
            HarnessAgent harness = client.AsHarnessAgent(new HarnessAgentOptions
            {
                AgentSkillsSource = gate, DisableWebSearch = true, DisableFileMemory = true,
                DisableTodoProvider = true, DisableAgentModeProvider = true,
            });
            AgentSession session = await harness.CreateSessionAsync();
            using (gate.BeginRun("H1")) await harness.RunAsync("one", session);
            using (gate.BeginRun("H2")) await harness.RunAsync("two", session);
            Check("HarnessAgentOptions.AgentSkillsSource: each run opened with BeginRun is evaluated once (H1, H2: one activation each)",
                gate.Log.Where(e => e.Operation == "activated").Select(e => e.Run).SequenceEqual(["H1", "H2"]) && client.Turns[0].ToolNames.Contains("load_skill"));

            // 15. Two gates in one flow (two agents, or a background agent) keep their own pins.
            string rootA = CopySkills(work, "gate-a"), rootB = CopySkills(work, "gate-b");
            SkillRegistry approvedA = new();
            approvedA.Approve(await ValidateAsync(rootA), "reviewer");
            GatedSkillsSource gateA = new(new AgentFileSkillsSource(rootA), approvedA);
            GatedSkillsSource gateB = new(new AgentFileSkillsSource(rootB), new SkillRegistry());
            int fromA, fromB;
            using (gateA.BeginRun("RA"))
            using (gateB.BeginRun("RB"))
            {
                fromA = (await gateA.GetSkillsAsync(context)).Count;
                fromB = (await gateB.GetSkillsAsync(context)).Count;
            }
            Check("two gates in one scope: A serves its approved skill under run RA; B, with an empty registry, serves none under RB",
                fromA == 1 && fromB == 0 && gateA.Log.Single() is { Run: "RA", Operation: "activated" } &&
                gateB.Log.Single() is { Run: "RB", Operation: "refused", Detail: "not validated" });
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }

        Console.WriteLine($"\nRESULT {passed}/{total}");
        return passed == total ? 0 : 1;
    }

    private static string CopySkills(string work, string name)
    {
        string target = Path.Combine(work, name);
        foreach (string file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "skills"), "*", SearchOption.AllDirectories))
        {
            string to = Path.Combine(target, Path.GetRelativePath(Path.Combine(AppContext.BaseDirectory, "skills"), file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.WriteAllText(to, SkillRegistry.Normalize(File.ReadAllText(file)));   // LF, whatever the checkout did
        }
        return target;
    }

    private static void Edit(string root, string from, string to)
    {
        string path = Path.Combine(root, Skill, "SKILL.md");
        string text = File.ReadAllText(path);
        if (!text.Contains(from)) throw new InvalidOperationException($"Self-test edit target not found: {from}");
        File.WriteAllText(path, text.Replace(from, to));
    }

    private static async Task<AgentSkillsSourceContext> ContextAsync()
    {
        ChatClientAgent agent = new(new ScriptedToolChatClient(_ => ScriptedToolChatClient.Say("ok")));
        return new AgentSkillsSourceContext(agent, await agent.CreateSessionAsync());
    }

    private static AgentSkillsProvider Builder(string root) =>
        new AgentSkillsProviderBuilder().UseFileSkill(root)
            .UseFileScriptRunner((_, _, _, _, _) => Task.FromResult<object?>(null)).Build();

    private static async Task<ValidationResult> ValidateAsync(string root)
    {
        IList<AgentSkill> skills = await SkillDiscovery.FromFolderAsync(root);
        return await SkillRegistry.ValidateAsync(skills.Single());
    }

    /// <summary>
    /// One run: load_skill, optionally read_skill_resource, then a final answer. Returns every
    /// tool result joined. pause blocks before load_skill; pauseBeforeRead blocks between the two.
    /// </summary>
    private static async Task<string> LoadAsync(AgentSkillsProvider provider, GatedSkillsSource? gate = null, string? run = null,
        bool readResource = false,
        (ManualResetEventSlim Reached, ManualResetEventSlim Go)? pause = null,
        (ManualResetEventSlim Reached, ManualResetEventSlim Go)? pauseBeforeRead = null)
    {
        using IDisposable? scope = gate?.BeginRun(run ?? "run");
        ScriptedToolChatClient client = null!;
        client = new(turn =>
        {
            int step = turn.Results.Count;
            if (step == 0 && pause is { } p) { p.Reached.Set(); p.Go.Wait(TimeSpan.FromSeconds(20)); }
            if (step == 1 && pauseBeforeRead is { } q) { q.Reached.Set(); q.Go.Wait(TimeSpan.FromSeconds(20)); }
            return step switch
            {
                0 => client.Call("load_skill", new Dictionary<string, object?> { ["skillName"] = Skill }),
                1 when readResource => client.Call("read_skill_resource",
                    new Dictionary<string, object?> { ["skillName"] = Skill, ["resourceName"] = Resource }),
                _ => ScriptedToolChatClient.Say("done"),
            };
        });
        ToolApprovalAgent agent = new(new ChatClientAgent(client, new ChatClientAgentOptions { AIContextProviders = [provider] }),
            new ToolApprovalAgentOptions { AutoApprovalRules = [AgentSkillsProvider.ReadOnlyToolsAutoApprovalRule] });
        await agent.RunAsync("Use the refund skill.", await agent.CreateSessionAsync());
        // Every tool result the model saw in this run (the last request carries them all).
        return string.Join("\n", client.Turns[^1].Results.Select(r => r.Result?.ToString()));
    }
}
