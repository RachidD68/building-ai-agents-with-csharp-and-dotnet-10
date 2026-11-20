using System.Net.Http.Json;
using System.Text.Json;
using Ch08.ActionPolicy;
using Ch18.ApprovalUi;
using Ch18.SseStatusStream;

namespace Ch18.AguiServer;

/// <summary>--self-test: the AG-UI host on a TestServer, a fake model, minted tokens, offline.</summary>
internal static class AguiSelfTest
{
    private static int _passed, _total;

    public static async Task<int> RunAsync()
    {
        Console.WriteLine("=== Ch18.AguiServer (self-test, offline, AG-UI 1.5.0-preview) ===\n");

        Console.WriteLine("-- 1. The thread id is a locator, not an authorization");
        await using (TestHost open = await TestHost.StartAsync(new ToolScriptChatClient(), guardThreads: false))
        {
            await AguiWire.RunAsync(open.Http, "/agui/assistant", open.Token("contoso", "E-1001"),
                AguiWire.Body("t-alice", "r1", ("m1", "My hotel receipt: 412 EUR, room 18")));
            (_, List<JsonElement> stolen) = await AguiWire.RunAsync(open.Http, "/agui/assistant", open.Token("contoso", "E-2002"),
                AguiWire.Body("t-alice", "r2", ("m2", "hello")));
            Check($"DEFECT REPRODUCED: no guard, bob posts alice's thread id and the model answers \"{AguiWire.Text(stolen).Trim()}\"",
                AguiWire.Text(stolen).Contains("412 EUR", StringComparison.Ordinal));
        }

        Dictionary<string, int> built = [];
        await using TestHost host = await TestHost.StartAsync(new ToolScriptChatClient(),
            agentBuilt: name => { lock (built) built[name] = built.GetValueOrDefault(name) + 1; });
        HttpClient http = host.Http;
        string alice = host.Token("contoso", "E-1001"), bob = host.Token("contoso", "E-2002");
        string rita = host.Token("contoso", "rita"), ana = host.Token("contoso", "ana"), fabrikamAlice = host.Token("fabrikam", "E-1001");

        await AguiWire.RunAsync(http, "/agui/assistant", alice, AguiWire.Body("t-alice", "r1", ("m1", "My hotel receipt: 412 EUR, room 18")));
        (int foreign, _) = await AguiWire.RunAsync(http, "/agui/assistant", bob, AguiWire.Body("t-alice", "r2", ("m2", "hello")));
        Check($"guarded: bob on alice's thread id: {foreign}", foreign == 404);
        (int sameSubject, _) = await AguiWire.RunAsync(http, "/agui/assistant", fabrikamAlice, AguiWire.Body("t-alice", "r3", ("m3", "hello")));
        Check($"fabrikam/E-1001 (same subject, other tenant) on it: {sameSubject}, refused before the thread is looked up", sameSubject == 403);

        Console.WriteLine("\n-- 1b. The guard reads the thread id the way MAF does");
        await using (TestHost loose = await TestHost.StartAsync(new ToolScriptChatClient(), readThreadIdLikeMaf: false))
        {
            await AguiWire.RunAsync(loose.Http, "/agui/assistant", loose.Token("contoso", "E-1001"),
                AguiWire.Body("t-alice", "r1", ("m1", "My hotel receipt: 412 EUR, room 18")));
            (int status, List<JsonElement> smuggled) = await AguiWire.RunAsync(loose.Http, "/agui/assistant", loose.Token("contoso", "E-2002"),
                AguiWire.WithProperty(AguiWire.Body("t-bob-1", "r2", ("m2", "hello")), "ThreadId", "t-alice"));
            Check($"DEFECT REPRODUCED: a case-sensitive guard checks 'threadId' (bob's t-bob-1), MAF reads the later 'ThreadId' (t-alice): {status}, \"{AguiWire.Text(smuggled).Trim()}\"",
                status == 200 && AguiWire.Text(smuggled).Contains("412 EUR", StringComparison.Ordinal));
        }
        int[] spelled = [.. await Task.WhenAll(new[] { "ThreadId", "THREADID" }.Select(async name =>
            (await AguiWire.RunAsync(http, "/agui/assistant", bob,
                AguiWire.WithProperty(AguiWire.Body($"t-bob-{name}", "r2", ("m2", "hello")), name, "t-alice"))).Status))];
        (int onlyCased, _) = await AguiWire.RunAsync(http, "/agui/assistant", bob,
            AguiWire.Body("t-alice", "r2", ("m2", "hello")).Replace("\"threadId\"", "\"ThreadId\"", StringComparison.Ordinal));
        (_, List<JsonElement> own) = await AguiWire.RunAsync(http, "/agui/assistant", alice, AguiWire.Body("t-alice", "r4", ("m4", "hello")));
        Check($"guarded: a second 'ThreadId' / 'THREADID' naming alice's thread: {spelled[0]} / {spelled[1]}; a lone 'ThreadId': {onlyCased}; alice's thread still holds only her messages: \"{AguiWire.Text(own).Trim()}\"",
            spelled.All(s => s == 400) && onlyCased == 400 && AguiWire.Text(own).Contains("I see 2 user message(s)", StringComparison.Ordinal));

        Console.WriteLine("\n-- 2. Selection only after authorization (rights loaded per request)");
        (int anon, _) = await AguiWire.RunAsync(http, "/agui/assistant", null, AguiWire.Body("t-x", "r1", ("m1", "hi")));
        (int bobReview, _) = await AguiWire.RunAsync(http, "/agui/review", bob, AguiWire.Body("t-bob-r", "r1", ("m1", "review")));
        (int ritaAssistant, _) = await AguiWire.RunAsync(http, "/agui/assistant", rita, AguiWire.Body("t-rita-a", "r1", ("m1", "hi")));
        (int ritaReview, _) = await AguiWire.RunAsync(http, "/agui/review", rita, AguiWire.Body("t-rita-r", "r1", ("m1", "review")));
        Check($"no token {anon}; bob on the review agent {bobReview}; rita on the employee agent {ritaAssistant}; rita on the review agent {ritaReview}",
            anon == 401 && bobReview == 403 && ritaAssistant == 403 && ritaReview == 200);

        Console.WriteLine("\n-- 3. AG-UI carries the response; the status stream carries the operation");
        (_, List<JsonElement> run) = await AguiWire.RunAsync(http, "/agui/assistant", alice,
            AguiWire.Body("t-alice-2", "run-1", ("m1", "Please submit my taxi receipt of 120 EUR")));
        string[] types = [.. run.Select(e => e.GetProperty("type").GetString()!).Distinct()];
        Console.WriteLine($"     AG-UI event types: {string.Join(", ", types)}");
        Check("RUN_STARTED ... RUN_FINISHED with the client's run id",
            types[0] == "RUN_STARTED" && types[^1] == "RUN_FINISHED" && run[^1].GetProperty("runId").GetString() == "run-1");
        Check($"the reply streams the tool's result: \"{AguiWire.Text(run).Trim()}\"",
            AguiWire.Text(run).Contains("waiting for a finance reviewer", StringComparison.Ordinal));
        StatusEvent? snap = await Snapshot(http, alice, "t-alice-2");
        Check($"after RUN_FINISHED the operation is {snap?.State} ({snap?.Reason}): not done",
            snap?.State == OperationState.AwaitingApproval && !OperationStates.IsTerminal(snap.State));
        Check("bob reads alice's thread status: 404", (await Snapshot(http, bob, "t-alice-2")) is null);
        Guid proposal = Guid.Parse(System.Text.RegularExpressions.Regex.Match(AguiWire.Text(run), "[0-9a-f-]{36}").Value);

        Console.WriteLine("\n-- 4. One agent instance, an identity per request");
        await AguiWire.RunAsync(http, "/agui/assistant", bob, AguiWire.Body("t-bob", "r1", ("m1", "submit a parking receipt of 40 EUR")));
        Task<(int, List<JsonElement>)> a = Task.Run(() => AguiWire.RunAsync(http, "/agui/assistant", alice, AguiWire.Body("t-alice-3", "r1", ("m1", "list my claims"))));
        Task<(int, List<JsonElement>)> b = Task.Run(() => AguiWire.RunAsync(http, "/agui/assistant", bob, AguiWire.Body("t-bob-2", "r1", ("m1", "list my claims"))));
        string aliceList = AguiWire.Text((await a).Item2), bobList = AguiWire.Text((await b).Item2);
        Check($"alice and bob list their claims at once (Task.Run): alice sees 120 only, bob 40 only",
            aliceList.Contains("120", StringComparison.Ordinal) && !aliceList.Contains("40 EUR", StringComparison.Ordinal)
            && bobList.Contains("40", StringComparison.Ordinal) && !bobList.Contains("120", StringComparison.Ordinal));
        Check($"agent instances built: assistant {built.GetValueOrDefault("assistant")}, review {built.GetValueOrDefault("review")}, after every AG-UI request above (registered Transient)",
            built.GetValueOrDefault("assistant") == 1 && built.GetValueOrDefault("review") == 1);
        StatusEvent? bobSnap = await Snapshot(http, bob, "t-bob");
        Check($"bob's 40 EUR: {bobSnap?.State} ({bobSnap?.Reason}), under the review limit", bobSnap?.State == OperationState.Completed);

        Console.WriteLine("\n-- 5. The review screen decides; the application commits");
        Check("bob opens the review of alice's claim: 404", (await http.SendAsync(Get($"/reviews/{proposal}", bob))).StatusCode == System.Net.HttpStatusCode.NotFound);
        ReviewView view = (await (await http.SendAsync(Get($"/reviews/{proposal}", rita))).Content.ReadFromJsonAsync<ReviewView>())!;
        Check($"rita sees {view.Amount} {view.Currency}, version {view.Version}", view.Amount == 120m);
        string first = await Decide(http, rita, proposal, new DecisionRequest(view.Version, view.Digest, "approve"));
        string again = await Decide(http, rita, proposal, new DecisionRequest(view.Version, view.Digest, "approve"));
        StatusEvent? done = await Snapshot(http, alice, "t-alice-2");
        Check($"rita approves: {first}; again: {again}; alice's thread: {done?.State} ({done?.Reason})",
            first == "200 Approved" && again == "200 Approved replayed" && done?.State == OperationState.Completed);
        ApprovalDesk desk = host.App.Services.GetRequiredService<ApprovalDesk>();
        Check($"commits stored: {desk.Commits.Count("operations")} (bob's 40 and alice's 120)", desk.Commits.Count("operations") == 2);

        Console.WriteLine("\n-- 6. A reviewer revoked mid-review");
        (_, List<JsonElement> second) = await AguiWire.RunAsync(http, "/agui/assistant", alice,
            AguiWire.Body("t-alice-4", "r1", ("m1", "submit a dinner receipt of 90 EUR")));
        Guid p2 = Guid.Parse(System.Text.RegularExpressions.Regex.Match(AguiWire.Text(second), "[0-9a-f-]{36}").Value);
        ReviewView v2 = (await (await http.SendAsync(Get($"/reviews/{p2}", ana))).Content.ReadFromJsonAsync<ReviewView>())!;
        host.Rights.Revoke("contoso", "ana", Roles.FinanceReviewer);
        (int anaAgent, _) = await AguiWire.RunAsync(http, "/agui/review", ana, AguiWire.Body("t-ana", "r1", ("m1", "review")));
        string anaPost = await Decide(http, ana, p2, new DecisionRequest(v2.Version, v2.Digest, "approve"));
        Check($"after the revocation: review agent {anaAgent}, decision POST {anaPost}", anaAgent == 403 && anaPost == "403 reviewer_not_authorized");

        Console.WriteLine($"\nRESULT {_passed}/{_total}");
        return _passed == _total ? 0 : 1;
    }

    private static HttpRequestMessage Get(string path, string token)
    {
        HttpRequestMessage r = new(HttpMethod.Get, path);
        r.Headers.Authorization = new("Bearer", token);
        return r;
    }

    private static async Task<StatusEvent?> Snapshot(HttpClient http, string token, string thread)
    {
        using HttpResponseMessage r = await http.SendAsync(Get($"/conversations/{thread}/snapshot", token));
        return r.IsSuccessStatusCode ? await r.Content.ReadFromJsonAsync<StatusEvent>(JsonSerializerOptions.Web) : null;
    }

    private static async Task<string> Decide(HttpClient http, string token, Guid id, DecisionRequest request)
    {
        using HttpRequestMessage r = new(HttpMethod.Post, $"/reviews/{id}/decision") { Content = JsonContent.Create(request) };
        r.Headers.Authorization = new("Bearer", token);
        using HttpResponseMessage response = await http.SendAsync(r);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return $"{(int)response.StatusCode} {doc.RootElement.GetProperty("result").GetString()}" +
               (doc.RootElement.GetProperty("replayed").GetBoolean() ? " replayed" : "");
    }

    private static void Check(string name, bool ok)
    {
        _total++;
        if (ok) _passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    }
}
