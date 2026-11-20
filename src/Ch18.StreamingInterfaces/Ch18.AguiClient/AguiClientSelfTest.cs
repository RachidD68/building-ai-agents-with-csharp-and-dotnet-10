using System.Net.Http.Json;
using System.Text.Json;
using Ch18.AguiServer;
using Ch18.ApprovalUi;
using Ch18.SseStatusStream;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.AGUI;
using Microsoft.Extensions.AI;

namespace Ch18.AguiClient;

/// <summary>--self-test: the client against Ch18.AguiServer in process, a fake model, offline.</summary>
internal static class AguiClientSelfTest
{
    private static int _passed, _total;

    public static async Task<int> RunAsync()
    {
        Console.WriteLine("=== Ch18.AguiClient (self-test, offline, AG-UI 1.5.0-preview) ===\n");
        await using TestHost host = await TestHost.StartAsync(new ToolScriptChatClient());
        string alice = host.Token("contoso", "E-1001"), bob = host.Token("contoso", "E-2002"), rita = host.Token("contoso", "rita");
        ContosoClient ui = new(host.Http, alice);

        Console.WriteLine("-- 1. A whole run, applied by identity");
        List<JsonElement> recorded = [];
        AguiRunView run = await ui.RunAsync("/agui/assistant", "t-1", "Please submit my taxi receipt of 120 EUR", onEvent: recorded.Add);
        Check($"RUN_FINISHED for run {run.RunId}; text \"{run.Text.Trim()[..40]}...\"; tools {string.Join(", ", run.Tools.Values)}",
            run.Finished && run.Text.Contains("waiting for a finance reviewer", StringComparison.Ordinal) && run.Tools.Values.Single() == "submit_claim: finished");
        AguiRunView replayed = new(run.RunId);
        JsonElement start = recorded.First(e => e.GetProperty("type").GetString() == "TEXT_MESSAGE_START");
        JsonElement foreign = JsonDocument.Parse("""{"type":"RUN_FINISHED","threadId":"t-1","runId":"run_other"}""").RootElement;
        foreach (JsonElement e in recorded.Take(recorded.Count - 1)) replayed.Apply(e);
        replayed.Apply(start);     // a duplicated START
        replayed.Apply(foreign);   // another run's end
        Check($"a duplicated TEXT_MESSAGE_START and another run's RUN_FINISHED are discarded ({replayed.Discarded}); not finished yet",
            replayed.Discarded == 2 && !replayed.Finished && replayed.Text == run.Text);
        await ui.ResyncAsync("t-1");
        Check($"screen after RUN_FINISHED: {ui.Screen(run)}", !ui.Status.ShowsDone && ui.Status.State == OperationState.AwaitingApproval);

        Console.WriteLine("\n-- 2. A cut run is discarded; the operation is resynced");
        AguiRunView cut = await ui.RunAsync("/agui/assistant", "t-2", "Please submit my dinner receipt of 75 EUR", cutAfter: 8);
        Check($"the connection drops after 8 events: NeedsResync, partial text discarded (\"{cut.Text}\")", cut.NeedsResync && cut.Text.Length == 0);
        StatusView? resynced = await ui.ResyncAsync("t-2");
        Check($"the snapshot says {resynced?.State} ({resynced?.Reason}): the claim exists, still not done",
            resynced?.State == OperationState.AwaitingApproval && !resynced.ShowsDone);
        AguiRunView holed = new(run.RunId);
        foreach (JsonElement e in recorded.Where(e => e.GetProperty("type").GetString() != "TEXT_MESSAGE_START")) holed.Apply(e);
        Check("content for a message that never started: NeedsResync", holed.NeedsResync && holed.Text.Length == 0);

        Console.WriteLine("\n-- 3. DONE only for the operation's terminal state");
        Guid id = host.Claims.Pending().First(p => host.Claims.Find(p)!.Thread == "t-1");
        HttpRequestMessage get = new(HttpMethod.Get, $"/reviews/{id}");
        get.Headers.Authorization = new("Bearer", rita);
        ReviewView view = (await (await host.Http.SendAsync(get)).Content.ReadFromJsonAsync<ReviewView>())!;
        await ui.ResyncAsync("t-1");
        Task follow = ui.FollowAsync("t-1", TimeSpan.FromSeconds(10));
        HttpRequestMessage post = new(HttpMethod.Post, $"/reviews/{id}/decision")
        {
            Content = JsonContent.Create(new DecisionRequest(view.Version, view.Digest, "approve")),
        };
        post.Headers.Authorization = new("Bearer", rita);
        await host.Http.SendAsync(post);
        await follow;
        Check($"rita approves; the client follows the status stream: {ui.Screen(run)}", ui.Status.ShowsDone && ui.Status.State == OperationState.Completed);

        Console.WriteLine("\n-- 4. Another identity on a valid thread id");
        ContosoClient bobUi = new(host.Http, bob);
        HttpRequestException? refused = null;
        try { await bobUi.RunAsync("/agui/assistant", "t-1", "hello"); }
        catch (HttpRequestException e) { refused = e; }
        Check($"bob posts to alice's thread: {(int?)refused?.StatusCode}; bob's snapshot of it: {(await bobUi.ResyncAsync("t-1") is null ? "404" : "200")}",
            (int?)refused?.StatusCode == 404 && await bobUi.ResyncAsync("t-1") is null);

        Console.WriteLine("\n-- 5. MAF's AGUIChatClient against the same host");
        HttpClient http = host.Http;
        http.DefaultRequestHeaders.Authorization = new("Bearer", alice);
        AIAgent remote = new AGUIChatClient(http, "/agui/assistant").AsAIAgent(name: "contoso-remote", instructions: null);
        System.Text.StringBuilder text = new();
        await foreach (AgentResponseUpdate u in remote.RunStreamingAsync("list my claims", await remote.CreateSessionAsync()))
            text.Append(u.Text);
        Check($"RunStreamingAsync over AG-UI: \"{text.ToString().Trim()[..Math.Min(60, text.ToString().Trim().Length)]}...\"",
            text.ToString().Contains("120", StringComparison.Ordinal) && text.ToString().Contains("75", StringComparison.Ordinal));

        Console.WriteLine($"\nRESULT {_passed}/{_total}");
        return _passed == _total ? 0 : 1;
    }

    private static void Check(string name, bool ok)
    {
        _total++;
        if (ok) _passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    }
}
