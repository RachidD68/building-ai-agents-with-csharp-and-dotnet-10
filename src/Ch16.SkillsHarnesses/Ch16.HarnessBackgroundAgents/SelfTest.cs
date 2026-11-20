using System.Diagnostics;
using AgenticAI.Testing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

/// <summary>
/// Offline checks: dotnet run -- --self-test. The coordinator is scripted; the
/// background agent runs on a FakeChatClient that either answers or hangs until cancelled.
/// </summary>
static class SelfTest
{
    private const string Start = "background_agents_start_task";
    private const string Wait = "background_agents_wait_for_first_completion";
    private const string Results = "background_agents_get_task_results";

    public static async Task<int> RunAsync()
    {
        Console.WriteLine("=== Background agents self-test (offline) ===\n");
        int passed = 0, total = 0;
        void Check(string name, bool ok)
        {
            total++; passed += ok ? 1 : 0;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
        }

        // 1-2. Where is the provider when you pass BackgroundAgents?
        FakeChatClient hanging = new(FakeReply.Hang("partial notes"));
        HarnessAgentOptions options = Options(new ChatClientAgent(hanging, name: "Researcher", instructions: "Notes."));
        HarnessAgent coordinator = Coordinator(options, [Start]);
        BackgroundAgentsProvider? firstEdition = options.AIContextProviders?.OfType<BackgroundAgentsProvider>().FirstOrDefault();
        BackgroundAgentsProvider? provider = coordinator.GetService<BackgroundAgentsProvider>();
        Check("DEFECT REPRODUCED: the first-edition lookup in options.AIContextProviders finds no provider, so release never runs",
            firstEdition is null);
        Check("coordinator.GetService<BackgroundAgentsProvider>() returns the harness's provider", provider is not null);

        // 3. The turn ends; the delegated work does not.
        AgentSession session = await coordinator.CreateSessionAsync();
        await coordinator.RunAsync("Get notes on the keyboard.", session);
        await Task.Delay(300);
        IReadOnlyList<BackgroundTaskInfo> left = provider!.GetIncompleteTasks(session);
        Check("DEFECT REPRODUCED: after the turn returns, the task is still Running and its model call is not cancelled",
            left.Count == 1 && left[0].Status == BackgroundTaskStatus.Running && hanging.Calls.Count == 1 && !hanging.Calls[0].SawCancellation);

        // 4. The fix.
        await provider.ReleaseSessionAsync(session, cancelRunning: true, timeout: TimeSpan.FromSeconds(10));
        Check("ReleaseSessionAsync: no incomplete task, and the background model call saw its cancellation",
            provider.GetIncompleteTasks(session).Count == 0 && hanging.Calls[0].SawCancellation);

        // 5. Waiting is bounded by WaitTimeout (1 s here), even on a task that never ends.
        FakeChatClient stuck = new(FakeReply.Hang());
        HarnessAgentOptions waitOptions = Options(new ChatClientAgent(stuck, name: "Researcher", instructions: "Notes."));
        HarnessAgent waiter = Coordinator(waitOptions, [Start, Wait]);
        AgentSession waitSession = await waiter.CreateSessionAsync();
        Stopwatch clock = Stopwatch.StartNew();
        AgentResponse waited = await waiter.RunAsync("Get notes, then wait for them.", waitSession);
        clock.Stop();
        string waitResult = ResultOf(waited, Wait);
        Console.WriteLine($"  wait result after {clock.Elapsed.TotalSeconds:F1} s: {waitResult}");
        Check("wait_for_first_completion returns within 10 s on a task that never ends (WaitTimeout = 1 s)",
            clock.Elapsed < TimeSpan.FromSeconds(10) && waitResult.Length > 0);
        await waiter.GetService<BackgroundAgentsProvider>()!.ReleaseSessionAsync(waitSession);

        // 6. A task that finishes: start, wait, collect.
        FakeChatClient answering = new(FakeReply.Text("Price $79.99; two-year warranty; quiet keys."));
        HarnessAgent collector = Coordinator(Options(new ChatClientAgent(answering, name: "Researcher", instructions: "Notes.")),
            [Start, Wait, Results]);
        AgentSession collectSession = await collector.CreateSessionAsync();
        AgentResponse collected = await collector.RunAsync("Get notes and bring them back.", collectSession);
        Check("start -> wait -> get_task_results returns the background agent's text; nothing left incomplete",
            ResultOf(collected, Results).Contains("$79.99") &&
            collector.GetService<BackgroundAgentsProvider>()!.GetIncompleteTasks(collectSession).Count == 0);

        // 7. Two sessions start work at the same time; releasing one leaves the other alone.
        FakeChatClient shared = new(FakeReply.Hang(), FakeReply.Hang());
        HarnessAgent desk = Coordinator(Options(new ChatClientAgent(shared, name: "Researcher", instructions: "Notes.")), [Start]);
        AgentSession a = await desk.CreateSessionAsync(), b = await desk.CreateSessionAsync();
        using Barrier rendezvous = new(2);
        await Task.WhenAll(
            Task.Run(async () => { rendezvous.SignalAndWait(); await desk.RunAsync("A", a); }),
            Task.Run(async () => { rendezvous.SignalAndWait(); await desk.RunAsync("B", b); }));
        BackgroundAgentsProvider deskProvider = desk.GetService<BackgroundAgentsProvider>()!;
        await deskProvider.ReleaseSessionAsync(a);
        bool isolated = deskProvider.GetIncompleteTasks(a).Count == 0 && deskProvider.GetIncompleteTasks(b).Count == 1;
        await deskProvider.ReleaseSessionAsync(b);
        Check("two sessions started concurrently: releasing A cancels A's task only; B's still runs until B is released",
            isolated && deskProvider.GetIncompleteTasks(b).Count == 0 && shared.Calls.All(c => c.SawCancellation));

        Console.WriteLine($"\nRESULT {passed}/{total}");
        return passed == total ? 0 : 1;
    }

    private static HarnessAgentOptions Options(AIAgent researcher) => new()
    {
        BackgroundAgents = [researcher],
        BackgroundAgentsProviderOptions = new BackgroundAgentsProviderOptions { WaitTimeout = TimeSpan.FromSeconds(1) },
        DisableWebSearch = true, DisableFileMemory = true, DisableTodoProvider = true,
        DisableAgentModeProvider = true, DisableAgentSkillsProvider = true,
    };

    /// <summary>A coordinator that calls the given tools in order, one per model call, then answers.</summary>
    private static HarnessAgent Coordinator(HarnessAgentOptions options, string[] plan)
    {
        ScriptedToolChatClient client = null!;
        client = new(turn =>
        {
            int step = turn.Results.Count;
            if (step >= plan.Length) return ScriptedToolChatClient.Say("done");
            return plan[step] switch
            {
                Start => client.Call(Start, new Dictionary<string, object?>
                    { ["agentName"] = "Researcher", ["input"] = "Notes on the keyboard.", ["description"] = "keyboard notes" }),
                Wait => client.Call(Wait, new Dictionary<string, object?> { ["taskIds"] = new[] { 1 } }),
                _ => client.Call(plan[step], new Dictionary<string, object?> { ["taskId"] = 1 }),
            };
        });
        return client.AsHarnessAgent(options);
    }

    private static string ResultOf(AgentResponse response, string tool)
    {
        List<AIContent> contents = [.. response.Messages.SelectMany(m => m.Contents)];
        string? id = contents.OfType<FunctionCallContent>().LastOrDefault(c => c.Name == tool)?.CallId;
        return contents.OfType<FunctionResultContent>().LastOrDefault(r => r.CallId == id)?.Result?.ToString() ?? "";
    }
}
