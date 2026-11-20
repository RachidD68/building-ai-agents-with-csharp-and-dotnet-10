using System.Threading.Channels;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;

namespace Ch18.SseStatusStream;

/// <summary>
/// --self-test: the status stream on an in-memory TestServer, a scripted
/// model, offline. Each failure of the chapter is reproduced with the naive
/// client first (DEFECT REPRODUCED), then the rule that prevents it.
/// </summary>
internal static class SseSelfTest
{
    private static int _passed, _total;

    public static async Task<int> RunAsync()
    {
        Console.WriteLine("=== Ch18.SseStatusStream (self-test, offline) ===\n");
        OperationLog log = new(retain: 32, clientQueue: 8);
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        await using WebApplication app = builder.Build();
        app.MapStatusStream("/conversations", log, (_, id) => id != "c-hidden");
        await app.StartAsync();
        HttpClient http = app.GetTestClient();

        Console.WriteLine("-- 1. Typed events; the response ends, the operation does not");
        AIAgent agent = new ScriptedChatClient().AsAIAgent(instructions: "Contoso expense assistant",
            tools: [AIFunctionFactory.Create(() => "Claims above 50 EUR wait for a finance reviewer.", "get_review_limit")]);
        string run1 = log.StartRun("c1");
        await StatusRunner.StreamResponseAsync(log, "c1", run1, agent, await agent.CreateSessionAsync(), "taxi, 120 EUR");
        log.SetState("c1", run1, OperationState.AwaitingApproval, "amount_above_review_limit");
        List<StatusEvent> events = await ReadAsync(http, "c1", 0, e => e.State == OperationState.AwaitingApproval);
        string types = string.Join(",", events.Select(e => e.Type).Distinct());
        Check($"event types in order: {types}", types == "state,tool,delta,response.done");
        Check("cursors are 1..n with no hole", events.Select(e => e.Cursor).SequenceEqual(Enumerable.Range(1, events.Count).Select(i => (long)i)));
        NaiveView naive = new();
        StatusView view = new();
        foreach (StatusEvent e in events) { naive.Apply(e); view.Apply(e); }
        Check($"DEFECT REPRODUCED: the naive UI shows '{naive.Shown}' while the operation is AwaitingApproval", naive.ShowsDone);
        Check($"the view shows {view.State}, not done; the text says 'submitted' and changes nothing",
            !view.ShowsDone && view.State == OperationState.AwaitingApproval && view.Text.Contains("submitted", StringComparison.Ordinal));
        log.SetState("c1", run1, OperationState.Completed, "paid");
        List<StatusEvent> rest = await ReadAsync(http, "c1", view.Cursor, _ => false);
        rest.ForEach(view.Apply);
        Check("Completed, set by the application after its commit, is the one event that shows done; the server then ends the stream",
            view.ShowsDone && rest.Count == 1 && rest[0].State == OperationState.Completed);

        Console.WriteLine("\n-- 2. Reconnect from a cursor");
        string run2 = log.StartRun("c2");
        string[] words = ["Your ", "claim ", "is ", "with ", "a ", "reviewer. "];
        foreach (string w in words[..3]) log.Delta("c2", run2, w);
        StatusView client = new();
        (await ReadAsync(http, "c2", 0, e => e.Cursor == 4)).ForEach(client.Apply);   // then the network drops
        foreach (string w in words[3..]) log.Delta("c2", run2, w);
        log.SetState("c2", run2, OperationState.AwaitingApproval, "amount_above_review_limit");
        (await ReadAsync(http, "c2", client.Cursor, e => e.State == OperationState.AwaitingApproval)).ForEach(client.Apply);
        Check($"resumed with Last-Event-ID {4}: text '{client.Text.Trim()}' applied once",
            client.Text == string.Concat(words) && client.Text == log.Snapshot("c2").Text);
        Check($"every event id applied exactly once: {client.AppliedIds.Count} ids for cursors 1..{log.Cursor("c2")}",
            client.AppliedIds.Count == log.Cursor("c2") && client.AppliedIds.Distinct().Count() == client.AppliedIds.Count);
        (await ReadAsync(http, "c2", 2, e => e.State == OperationState.AwaitingApproval)).ForEach(client.Apply);
        Check($"an overlapping replay (from cursor 2) is discarded: {client.Discarded} events, text unchanged",
            client.Discarded == 6 && client.Text == string.Concat(words));
        NaiveView naive2 = new();
        naive2.Apply(log.Snapshot("c2"));
        (await ReadAsync(http, "c2", 0, e => e.State == OperationState.AwaitingApproval)).ForEach(naive2.Apply);
        Check($"DEFECT REPRODUCED: snapshot, then a replay from 0 doubles the text ({naive2.Text.Length} chars for {string.Concat(words).Length})",
            naive2.Text == string.Concat(words) + string.Concat(words));

        Console.WriteLine("\n-- 3. A cursor older than the retained window gets a snapshot");
        string run3 = log.StartRun("c3");
        for (int i = 0; i < 80; i++) log.Delta("c3", run3, $"w{i} ");
        List<StatusEvent> afterGap = await ReadAsync(http, "c3", 3, _ => true);
        StatusView late = new();
        late.Apply(afterGap[0]);
        Check($"cursor 3, window of 32: the first event is a snapshot at cursor {afterGap[0].Cursor}",
            afterGap[0].Type == EventTypes.Snapshot && afterGap[0].Cursor == log.Cursor("c3"));
        Check("the snapshot carries the whole text (80 words)", late.Text.Split(" ", StringSplitOptions.RemoveEmptyEntries).Length == 80);
        StatusView holed = new();
        foreach (StatusEvent e in (await ReadAsync(http, "c2", 0, e => e.State == OperationState.AwaitingApproval)).Where(e => e.Cursor != 3))
            holed.Apply(e);
        Check("a hole in the cursors (3 missing) sets NeedsResync instead of applying past it", holed.NeedsResync && holed.Cursor == 2);

        Console.WriteLine("\n-- 4. An older run's outcome does not end the newer run");
        string runA = log.StartRun("c4");
        log.Delta("c4", runA, "Submitting... ");
        string runB = log.StartRun("c4");   // the user retried
        log.Delta("c4", runB, "Checking your receipt. ");
        log.SetState("c4", runA, OperationState.Completed, "late_outcome_of_first_run");
        List<StatusEvent> c4 = await ReadAsync(http, "c4", 0, e => e.Cursor == log.Cursor("c4"));
        NaiveView naive4 = new();
        StatusView view4 = new();
        foreach (StatusEvent e in c4) { naive4.Apply(e); view4.Apply(e); }
        Check("DEFECT REPRODUCED: the naive UI shows Completed for the retry", naive4.ShowsDone);
        Check($"the view stays on run B, Running, not done (discarded {view4.Discarded} event of run A)",
            view4.RunId == runB && view4.State == OperationState.Running && !view4.ShowsDone && view4.Discarded == 1);
        Task<List<StatusEvent>> open = ReadAsync(http, "c4", log.Cursor("c4"), _ => false, TimeSpan.FromSeconds(5));
        log.SetState("c4", runB, OperationState.Failed, "receipt_unreadable");
        List<StatusEvent> ended = await open;
        Check("the server's stream ended on run B's Failed, not on run A's Completed",
            ended.Count == 1 && ended[0].RunId == runB && ended[0].State == OperationState.Failed);

        Console.WriteLine("\n-- 5. A slow client: the queue is bounded");
        string run5 = log.StartRun("c5");
        Subscription slow = log.Subscribe("c5", log.Cursor("c5"));   // connected, never reads
        for (int i = 0; i < 1000; i++) log.Delta("c5", run5, ".");
        Check($"1000 events, queue bound {log.ClientQueue}: peak {slow.PeakQueued}, then the client is dropped",
            slow.PeakQueued <= log.ClientQueue && slow.Overflowed);
        int drained = 0;
        await foreach (StatusEvent _ in slow.Queue.Reader.ReadAllAsync()) drained++;
        Check($"it holds {drained} queued events, no more", drained == log.ClientQueue);
        string run5b = log.StartRun("c5b");
        await using (IAsyncEnumerator<System.Net.ServerSentEvents.SseItem<StatusEvent>> stalled =
            StatusEndpoints.Stream(log, "c5b", log.Cursor("c5b"), CancellationToken.None).GetAsyncEnumerator())
        {
            ValueTask<bool> first = stalled.MoveNextAsync();   // the endpoint's own iterator, then the client stops reading
            for (int i = 0; i < 1000; i++) log.Delta("c5b", run5b, ".");
            await first;
            List<string> tail = [stalled.Current.EventType];
            while (await stalled.MoveNextAsync()) tail.Add(stalled.Current.EventType);
            Check($"the endpoint's stream for a stalled client: {tail.Count - 1} events, then '{tail[^1]}'; the client reconnects from its cursor",
                tail[^1] == EventTypes.Resync && tail.Count <= log.ClientQueue + 2);
        }
        Channel<StatusEvent> unbounded = Channel.CreateUnbounded<StatusEvent>();
        for (int i = 0; i < 1000; i++) unbounded.Writer.TryWrite(log.Snapshot("c5"));
        Check($"DEFECT REPRODUCED: an unbounded queue holds {unbounded.Reader.Count} events for one stalled client",
            unbounded.Reader.Count == 1000);

        Console.WriteLine("\n-- 6. Cancellation is a request; the outcome is observed");
        string run6 = log.StartRun("c6");
        log.Delta("c6", run6, "Paying 30 EUR. ");
        NaiveView naive6 = new();
        naive6.ClickCancel();
        log.CancelRequested("c6", run6);
        StatusView view6 = new();
        (await ReadAsync(http, "c6", 0, e => e.Type == EventTypes.CancelRequested)).ForEach(view6.Apply);
        Check("after the click: 'cancel requested', not done", view6.CancelPending && !view6.ShowsDone);
        log.SetState("c6", run6, OperationState.Completed, "committed_before_cancel");   // the effect had committed
        (await ReadAsync(http, "c6", view6.Cursor, _ => false)).ForEach(view6.Apply);
        Check($"DEFECT REPRODUCED: the naive UI shows {naive6.Shown}; the payment committed", naive6.Shown == "Cancelled");
        Check($"the view shows {view6.State} ({view6.Reason})", view6.State == OperationState.Completed && view6.Reason == "committed_before_cancel");
        string run7 = log.StartRun("c6");
        log.CancelRequested("c6", run7);
        log.SetState("c6", run7, OperationState.OutcomeUnknown, "provider_timeout");
        StatusView view7 = new();
        view7.Apply(log.Snapshot("c6"));
        Check("a provider timeout after the request: OutcomeUnknown, still not done", view7.State == OperationState.OutcomeUnknown && !view7.ShowsDone);

        Console.WriteLine("\n-- 7. Concurrent writers, one order");
        string run8 = log.StartRun("c8");
        long before = log.Cursor("c8");
        Task[] writers = [.. Enumerable.Range(0, 4).Select(w => Task.Run(() =>
        {
            for (int i = 0; i < 250; i++) log.Delta("c8", run8, $"{w}");
        }))];
        await Task.WhenAll(writers);
        Check($"4 writers on Task.Run, 1000 deltas: cursors {before + 1}..{log.Cursor("c8")}, each once",
            log.Cursor("c8") - before == 1000 && log.Snapshot("c8").Text!.Length == 1000);

        Console.WriteLine("\n-- 8. The host decides who reads");
        using HttpResponseMessage hidden = await http.GetAsync("/conversations/c-hidden/events");
        using HttpResponseMessage hiddenSnap = await http.GetAsync("/conversations/c-hidden/snapshot");
        Check("a conversation the caller may not read: 404 on events and snapshot (same as no such id)",
            (int)hidden.StatusCode == 404 && (int)hiddenSnap.StatusCode == 404);

        Console.WriteLine($"\nRESULT {_passed}/{_total}");
        return _passed == _total ? 0 : 1;
    }

    // Reads until the predicate holds or the server ends the stream; a timeout is a cut connection.
    private static async Task<List<StatusEvent>> ReadAsync(HttpClient http, string conversation, long after,
        Func<StatusEvent, bool> stop, TimeSpan? timeout = null)
    {
        List<StatusEvent> got = [];
        using CancellationTokenSource cts = new(timeout ?? TimeSpan.FromSeconds(2));
        try
        {
            await foreach (StatusEvent e in StatusStreamClient.ReadAsync(http, $"/conversations/{conversation}/events", after, ct: cts.Token))
            {
                got.Add(e);
                if (stop(e)) break;
            }
        }
        catch (OperationCanceledException) { }
        return got;
    }

    private static void Check(string name, bool ok)
    {
        _total++;
        if (ok) _passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    }
}
