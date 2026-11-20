using AgenticAI.Common.Extensions;
using AgenticAI.Testing;
using Ch04.FirstAgent;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ch04.AgentTests;

/// <summary>
/// The same contracts against the configured model. A RecordingChatClient sits
/// between the agent and the provider, so the checks read what was really sent.
/// The answers are printed, not graded: a contract check cannot judge them.
/// </summary>
internal static class LiveChecks
{
    public static async Task<int> RunAsync(string[] args)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args.Where(a => a != "--live").ToArray());
        builder.Services.AddLlmChatClient(builder.Configuration);
        using IHost host = builder.Build();
        RecordingChatClient model = new(host.Services.GetRequiredService<IChatClient>());
        ChatClientAgent agent = ContosoPolicy.Create(model);

        Console.WriteLine("=== The first agent's first test (live) ===");
        Harness t = new();

        t.Section("RunAsync");
        RunOutcome answer = await AgentRuns.RunAsync(
            () => agent.RunAsync("Is a 42 EUR taxi receipt approved automatically?"), CancellationToken.None);
        Show(answer);
        t.Expect("the policy instructions reached the provider call",
            model.Calls[^1].Options?.Instructions == ContosoPolicy.Instructions);
        t.Expect("the run is Completed with some text", answer.Status == RunStatus.Completed && answer.Text.Length > 0);

        t.Section("RunStreamingAsync");
        RunOutcome streamed = await AgentRuns.CollectAsync(
            agent.RunStreamingAsync("Who reviews a 180 EUR hotel claim?"), CancellationToken.None);
        Show(streamed);
        t.Expect("the streamed run is Completed with some text",
            streamed.Status == RunStatus.Completed && streamed.Text.Length > 0);

        t.Section("a 12-token ceiling for one call");
        RunOutcome cut = await AgentRuns.CollectAsync(
            agent.RunStreamingAsync("Explain the whole expense policy.",
                options: new ChatClientAgentRunOptions(new ChatOptions { MaxOutputTokens = 12 })),
            CancellationToken.None);
        Show(cut);
        t.Expect("the provider reports Length and the run is Truncated", cut.Status == RunStatus.Truncated);

        t.Section("cancelled after the first piece of text");
        using CancellationTokenSource cts = new();
        RunOutcome cancelled = await AgentRuns.CollectAsync(
            agent.RunStreamingAsync("Explain every rule of the expense policy in detail.", cancellationToken: cts.Token),
            cts.Token, _ => cts.Cancel());
        Show(cancelled);
        t.Expect("the run is Cancelled and its partial text is not an answer",
            cancelled.Status == RunStatus.Cancelled && !cancelled.IsSuccess);

        return t.Finish();
    }

    private static void Show(RunOutcome o) =>
        Console.WriteLine($"   {o.Status} (finish: {o.FinishReason?.Value ?? "none"}): {o.Text.Trim()}" +
                          (o.Error is null ? "" : $" [{o.Error.GetType().Name}: {o.Error.Message}]"));
}
