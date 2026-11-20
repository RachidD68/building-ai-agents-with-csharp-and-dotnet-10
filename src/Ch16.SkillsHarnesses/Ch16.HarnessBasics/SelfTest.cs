using AgenticAI.Testing;
using Ch16.Harness;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

/// <summary>Offline checks: dotnet run -- --self-test. A scripted client stands in for the model.</summary>
static class SelfTest
{
    public static async Task<int> RunAsync()
    {
        Console.WriteLine("=== Harness basics self-test (offline) ===\n");
        int passed = 0, total = 0;
        void Check(string name, bool ok)
        {
            total++; passed += ok ? 1 : 0;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
        }

        string original = Directory.GetCurrentDirectory();
        string work = Path.Combine(Path.GetTempPath(), "ch16-basics-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        Directory.SetCurrentDirectory(work);   // the harness writes relative to the working directory
        try
        {
            // 1-2. A bare HarnessAgentOptions, as a first attempt would write it.
            (ScriptedTurn bare, _) = await RunOnceAsync(new HarnessAgentOptions());
            AITool? search = bare.Options?.Tools?.FirstOrDefault(t => t.Name == "web_search");
            Check("DEFECT REPRODUCED: default options send web_search (HostedWebSearchTool), which Ollama cannot serve",
                search is HostedWebSearchTool);
            Check("DEFECT REPRODUCED: a default run creates agent-file-memory under the working directory",
                Directory.Exists(Path.Combine(work, "agent-file-memory")));
            Directory.Delete(Path.Combine(work, "agent-file-memory"), recursive: true);

            // 3. The fix for the first: one switch, one tool gone, nothing else touched.
            (ScriptedTurn noSearch, _) = await RunOnceAsync(new HarnessAgentOptions { DisableWebSearch = true });
            Check("DisableWebSearch removes web_search and nothing else",
                bare.ToolNames.Except(noSearch.ToolNames).SequenceEqual(["web_search"]) &&
                !noSearch.ToolNames.Except(bare.ToolNames).Any());

            // 4. The fix for the second (the run above had file memory on, so start clean).
            foreach (string dir in Directory.GetDirectories(work)) Directory.Delete(dir, recursive: true);
            (ScriptedTurn noMemory, _) = await RunOnceAsync(
                new HarnessAgentOptions { DisableWebSearch = true, DisableFileMemory = true });
            Check("DisableFileMemory: no file_memory_* tool and nothing written under the working directory",
                !noMemory.ToolNames.Any(t => t.StartsWith("file_memory_")) && !Directory.EnumerateFileSystemEntries(work).Any());

            // 5. The default column is read from a bare HarnessAgentOptions, not typed by hand.
            HarnessAgentOptions configured = new() { DisableWebSearch = true, DisableFileMemory = true };
            IReadOnlyList<Capability> report = CapabilityReport.Build(configured, noMemory.ToolNames, []);
            string[] onByDefault = [.. report.Where(c => c.OnByDefault).Select(c => c.Name)];
            Check("default column: web search, to-do, modes, skills, file memory, approval, telemetry on; file access and background agents off",
                onByDefault.SequenceEqual(["hosted web search", "to-do list", "agent modes", "skills", "file memory",
                                           "tool auto-approval", "OpenTelemetry"]));

            // 6. Exercised means called: the script calls one to-do tool, once.
            (_, AgentResponse todoRun) = await RunOnceAsync(configured, callTool: "todos_add",
                arguments: new Dictionary<string, object?> { ["title"] = "Check the carrier" });
            IReadOnlyList<Capability> used = CapabilityReport.Build(configured, noMemory.ToolNames,
                CapabilityReport.Called(todoRun.Messages));
            Check("called column counts only the tools the run called (to-do list: 1, every other row: 0)",
                used.Single(c => c.Name == "to-do list").Called == 1 && used.Where(c => c.Name != "to-do list").All(c => c.Called == 0));

            // 7. Instruction layers as 1.28 sends them.
            string harnessDefault = HarnessAgent.DefaultInstructions;
            string none = await InstructionsAsync(null, null);
            string job = await InstructionsAsync("JOB: Contoso policy desk.", "VOICE: be brief.");
            Check("HarnessInstructions replaces DefaultInstructions; ChatOptions.Instructions follows it",
                none == harnessDefault && !job.Contains(harnessDefault) &&
                job.IndexOf("JOB:", StringComparison.Ordinal) < job.IndexOf("VOICE:", StringComparison.Ordinal));

            // 8. Sessions work as they do for any AIAgent.
            ScriptedToolChatClient client = new(_ => ScriptedToolChatClient.Say("ok"));
            HarnessAgent agent = client.AsHarnessAgent(Minimal());
            AgentSession session = await agent.CreateSessionAsync();
            await agent.RunAsync("First question.", session);
            await agent.RunAsync("Second question.", session);
            Check("the second turn on one session sends the first turn as history",
                client.Turns[1].Messages.Any(m => m.Text.Contains("First question.")));
        }
        finally
        {
            Directory.SetCurrentDirectory(original);
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }

        Console.WriteLine($"\nRESULT {passed}/{total}");
        return passed == total ? 0 : 1;
    }

    private static HarnessAgentOptions Minimal() => new()
    {
        DisableWebSearch = true, DisableFileMemory = true, DisableTodoProvider = true,
        DisableAgentModeProvider = true, DisableAgentSkillsProvider = true,
    };

    private static async Task<(ScriptedTurn First, AgentResponse Response)> RunOnceAsync(
        HarnessAgentOptions options, string? callTool = null, IDictionary<string, object?>? arguments = null)
    {
        ScriptedToolChatClient client = null!;
        client = new(turn => callTool is not null && turn.Results.Count == 0
            ? client.Call(callTool, arguments)
            : ScriptedToolChatClient.Say("ok"));
        HarnessAgent agent = client.AsHarnessAgent(options);
        AgentResponse response = await agent.RunAsync("Where is order ORD-3003?", await agent.CreateSessionAsync());
        return (client.Turns[0], response);
    }

    private static async Task<string> InstructionsAsync(string? harness, string? voice)
    {
        HarnessAgentOptions options = Minimal();
        options.HarnessInstructions = harness;
        options.ChatOptions = new ChatOptions { Instructions = voice };
        (ScriptedTurn first, _) = await RunOnceAsync(options);
        return first.Options?.Instructions ?? "";
    }
}
