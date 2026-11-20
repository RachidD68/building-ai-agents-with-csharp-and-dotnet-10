using System.Text;
using AgenticAI.Testing;
using Ch04.FirstAgent;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Ch04.AgentTests;

/// <summary>Every check runs against a <see cref="FakeChatClient"/>: no network, no model.</summary>
internal static class OfflineChecks
{
    private const string Question = "Is a 42 EUR taxi receipt approved automatically?";

    public static async Task<int> RunAsync()
    {
        Console.WriteLine("=== The first agent's first test (self-test) ===");
        Harness t = new();
        await WhatReachesTheModel(t);
        await StreamedRuns(t);
        await NonStreamedRuns(t);
        return t.Finish();
    }

    // ------------------------------------------------------------------
    // 1. The instructions, the messages and the options the model receives.
    // ------------------------------------------------------------------
    private static async Task WhatReachesTheModel(Harness t)
    {
        t.Section("what reaches the model");
        FakeChatClient model = new(
            FakeReply.Text("Yes: 42 EUR is under the 50.00 EUR limit."),
            FakeReply.Text("My hotel claim is noted."),
            FakeReply.Text("A finance reviewer approves it."),
            FakeReply.Text("A finance reviewer approves it."));
        ChatClientAgent agent = ContosoPolicy.Create(model);

        AgentResponse first = await agent.RunAsync(Question);
        FakeCall call = model.Calls[0];
        t.Expect("the policy instructions reach the model",
            call.Options?.Instructions == ContosoPolicy.Instructions);
        t.Expect("the question is the last message, sent once",
            call.Messages[^1].Role == ChatRole.User && call.Messages[^1].Text == Question
            && call.Messages.Count(m => m.Role == ChatRole.User) == 1);
        t.Expect("the reply comes back as the response text",
            first.Text == "Yes: 42 EUR is under the 50.00 EUR limit.");

        AgentSession session = await agent.CreateSessionAsync();
        await agent.RunAsync("My hotel claim is 180 EUR.", session);
        await agent.RunAsync("Who has to approve it?", session);
        await agent.RunAsync("Who has to approve it?");
        IReadOnlyList<ChatMessage> withSession = model.Calls[2].Messages;
        IReadOnlyList<ChatMessage> without = model.Calls[3].Messages;
        Console.WriteLine($"   with a session the model got {withSession.Count} messages, without one {without.Count}");
        t.Expect("with a session, the second run carries the first exchange",
            withSession.Any(m => m.Text == "My hotel claim is 180 EUR.")
            && withSession.Any(m => m.Role == ChatRole.Assistant && m.Text == "My hotel claim is noted."));
        t.Expect("without a session, a run sends no history",
            without.Count(m => m.Role != ChatRole.System) == 1);

        FakeChatClient tuned = new(FakeReply.Text("a"), FakeReply.Text("b"));
        ChatClientAgent pinned = new(tuned, new ChatClientAgentOptions
        {
            Name = "Pinned",
            ChatOptions = new ChatOptions
            {
                Instructions = ContosoPolicy.Instructions, Temperature = 0.7f, MaxOutputTokens = 300,
            },
        });
        await pinned.RunAsync(Question, options: new ChatClientAgentRunOptions(new ChatOptions { Temperature = 0f }));
        await pinned.RunAsync(Question);
        ChatOptions? perCall = tuned.Calls[0].Options, next = tuned.Calls[1].Options;
        Console.WriteLine($"   per call: temperature {perCall?.Temperature}, max tokens {perCall?.MaxOutputTokens}; " +
                          $"next call: temperature {next?.Temperature}, max tokens {next?.MaxOutputTokens}");
        t.Expect("a per-call option wins for that call and the agent's other defaults stay",
            perCall?.Temperature == 0f && perCall.MaxOutputTokens == 300
            && perCall.Instructions == ContosoPolicy.Instructions);
        t.Expect("the next call is back to the agent's defaults",
            next?.Temperature == 0.7f && next.MaxOutputTokens == 300);

        FakeChatClient quickModel = new(FakeReply.Text("No."));
        AIAgent quick = quickModel.AsAIAgent(instructions: ContosoPolicy.Instructions, name: "ContosoQuick");
        await quick.RunAsync("Can you pay my claim now?");
        t.Expect("AsAIAgent sends the same instructions",
            quickModel.Calls[0].Options?.Instructions == ContosoPolicy.Instructions);
    }

    // ------------------------------------------------------------------
    // 2. Streaming: one terminal state per run, partial text never a success.
    // ------------------------------------------------------------------
    private static async Task StreamedRuns(Harness t)
    {
        t.Section("streamed runs end in exactly one state");
        string[] chunks = ["Claims up to ", "50.00 EUR ", "are approved automatically."];

        RunOutcome done = await Collect(FakeReply.Stream(ChatFinishReason.Stop, chunks));
        Show(done);
        t.Expect("a full stream is Completed and keeps every chunk",
            done.Status == RunStatus.Completed && done.Text == string.Concat(chunks));

        RunOutcome cut = await Collect(FakeReply.Stream(ChatFinishReason.Length, chunks[..2]));
        Show(cut);
        t.Expect("finish reason Length is Truncated, not a success",
            cut.Status == RunStatus.Truncated && !cut.IsSuccess && cut.Text == "Claims up to 50.00 EUR ");

        // The defect first: a reader that only keeps a buffer.
        using (CancellationTokenSource cts = new())
        {
            ChatClientAgent agent = ContosoPolicy.Create(new FakeChatClient(FakeReply.Hang(chunks[0])));
            string naive = await NaiveReadAsync(agent.RunStreamingAsync(Question, cancellationToken: cts.Token), cts);
            Console.WriteLine($"   buffer-only reader returned \"{naive}\" and no status");
            t.Expect("DEFECT REPRODUCED: a buffer-only reader returns the partial text of a cancelled run as its answer",
                naive == chunks[0]);
        }

        FakeChatClient hanging = new(FakeReply.Hang(chunks[0]));
        using (CancellationTokenSource cts = new())
        {
            ChatClientAgent agent = ContosoPolicy.Create(hanging);
            RunOutcome cancelled = await AgentRuns.CollectAsync(
                agent.RunStreamingAsync(Question, cancellationToken: cts.Token), cts.Token, _ => cts.Cancel());
            Show(cancelled);
            t.Expect("cancelled mid-stream is Cancelled; the partial text is kept, not called a success",
                cancelled.Status == RunStatus.Cancelled && !cancelled.IsSuccess && cancelled.Text == chunks[0]);
            t.Expect("the caller's token reached the model call and stopped it", hanging.Calls[0].SawCancellation);
        }

        // A provider that ends the stream quietly once the token is cancelled: no
        // exception, no finish reason. The live run against Ollama did exactly this.
        using (CancellationTokenSource cts = new())
        {
            ChatClientAgent agent = ContosoPolicy.Create(new FakeChatClient(FakeReply.HangThenEndQuietly(chunks[0])));
            RunOutcome blind = await AgentRuns.CollectAsync(
                agent.RunStreamingAsync(Question, cancellationToken: cts.Token), CancellationToken.None, _ => cts.Cancel());
            Show(blind);
            t.Expect("DEFECT REPRODUCED: judged by the stream alone, a quietly ended cancelled run looks Completed",
                blind.Status == RunStatus.Completed && blind.FinishReason is null);
        }
        using (CancellationTokenSource cts = new())
        {
            ChatClientAgent agent = ContosoPolicy.Create(new FakeChatClient(FakeReply.HangThenEndQuietly(chunks[0])));
            RunOutcome quiet = await AgentRuns.CollectAsync(
                agent.RunStreamingAsync(Question, cancellationToken: cts.Token), cts.Token, _ => cts.Cancel());
            Show(quiet);
            t.Expect("with the caller's token, the same quiet end is Cancelled", quiet.Status == RunStatus.Cancelled);
        }
        using (CancellationTokenSource budget = new(TimeSpan.FromMilliseconds(100)))
        {
            ChatClientAgent agent = ContosoPolicy.Create(new FakeChatClient(FakeReply.HangThenEndQuietly(chunks[0])));
            RunOutcome quietBudget = await AgentRuns.CollectAsync(
                agent.RunStreamingAsync(Question, cancellationToken: budget.Token), CancellationToken.None,
                runToken: budget.Token);
            t.Expect("a quiet end after the time budget ran out is Failed",
                quietBudget.Status == RunStatus.Failed && quietBudget.Error is OperationCanceledException);
        }

        HttpRequestException dropped = new("connection reset by the model server");
        RunOutcome failed = await Collect(FakeReply.Throw(dropped, chunks[0]));
        Show(failed);
        t.Expect("an error mid-stream is Failed and keeps the exception as thrown",
            failed.Status == RunStatus.Failed && ReferenceEquals(failed.Error, dropped));

        using (CancellationTokenSource caller = new())
        using (CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(caller.Token))
        {
            budget.CancelAfter(TimeSpan.FromMilliseconds(100));
            ChatClientAgent agent = ContosoPolicy.Create(new FakeChatClient(FakeReply.Hang(chunks[0])));
            RunOutcome timedOut = await AgentRuns.CollectAsync(
                agent.RunStreamingAsync(Question, cancellationToken: budget.Token), caller.Token);
            Show(timedOut);
            t.Expect("a time budget that runs out is Failed, not Cancelled: the caller did not cancel",
                timedOut.Status == RunStatus.Failed && timedOut.Error is OperationCanceledException);
        }
    }

    // ------------------------------------------------------------------
    // 3. RunAsync: the same contracts without streaming.
    // ------------------------------------------------------------------
    private static async Task NonStreamedRuns(Harness t)
    {
        t.Section("non-streamed runs");
        HttpRequestException refused = new("model server returned 503");
        ChatClientAgent failing = ContosoPolicy.Create(new FakeChatClient(FakeReply.Throw(refused)));
        Exception? seen = null;
        try
        {
            await failing.RunAsync(Question);
        }
        catch (HttpRequestException ex)
        {
            seen = ex;
        }
        t.Expect("RunAsync lets the provider's exception through unchanged", ReferenceEquals(seen, refused));

        FakeChatClient hanging = new(FakeReply.Hang());
        using (CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(100)))
        {
            ChatClientAgent agent = ContosoPolicy.Create(hanging);
            RunOutcome outcome = await AgentRuns.RunAsync(() => agent.RunAsync(Question, cancellationToken: cts.Token), cts.Token);
            t.Expect("a cancelled RunAsync is Cancelled and the model call saw the token",
                outcome.Status == RunStatus.Cancelled && hanging.Calls[0].SawCancellation);
        }

        ChatClientAgent cutOff = ContosoPolicy.Create(
            new FakeChatClient(FakeReply.Text("Claims up to 50.00", ChatFinishReason.Length)));
        RunOutcome truncated = await AgentRuns.RunAsync(() => cutOff.RunAsync(Question), CancellationToken.None);
        Show(truncated);
        t.Expect("AgentResponse.FinishReason carries Length through, so RunAsync is Truncated too",
            truncated.Status == RunStatus.Truncated && truncated.FinishReason == ChatFinishReason.Length);
    }

    private static Task<RunOutcome> Collect(FakeReply reply) =>
        AgentRuns.CollectAsync(
            ContosoPolicy.Create(new FakeChatClient(reply)).RunStreamingAsync(Question), CancellationToken.None);

    /// <summary>The defect: catching the cancellation and returning whatever was buffered.</summary>
    private static async Task<string> NaiveReadAsync(IAsyncEnumerable<AgentResponseUpdate> updates, CancellationTokenSource cts)
    {
        StringBuilder buffer = new();
        try
        {
            await foreach (AgentResponseUpdate update in updates)
            {
                buffer.Append(update.Text);
                cts.Cancel();                    // the user presses Stop after the first chunk
            }
        }
        catch (OperationCanceledException)
        {
        }
        return buffer.ToString();
    }

    private static void Show(RunOutcome o) =>
        Console.WriteLine($"   {o.Status,-9} finish={o.FinishReason?.Value ?? "none",-6} text=\"{o.Text}\"" +
                          (o.Error is null ? "" : $" error={o.Error.GetType().Name}"));
}
