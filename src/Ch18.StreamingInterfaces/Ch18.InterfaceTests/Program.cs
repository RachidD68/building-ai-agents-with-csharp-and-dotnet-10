// ============================================================================
// Ch18.InterfaceTests: the chapter's interface contract, end to end, offline.
// ----------------------------------------------------------------------------
//   1 replay          a client drops, the operation moves on, the client resumes
//                     from its cursor: every event id applied once
//   2 stale binding   the arguments change under an open review screen: the old
//                     version is refused, the new one commits with its amount
//   3 double click    two approve POSTs at once (Task.Run): one decision, one
//                     replay, one commit, one Completed event
//   4 slow client     a stalled subscriber stays at its bound, is dropped, and
//                     resyncs from a snapshot
//   5 revoked         a reviewer loses the role mid-review: agent, screen, POST
//   6 wrong identity  a valid thread id or proposal id under another identity
//
//   dotnet run -- --self-test   (dotnet run alone does the same)
// Fake model, TestServer, minted tokens. AG-UI 1.5.0-preview.
// ============================================================================

using System.Net.Http.Json;
using System.Text.Json;
using Ch08.ActionPolicy;
using Ch18.AguiClient;
using Ch18.AguiServer;
using Ch18.ApprovalUi;
using Ch18.SseStatusStream;

_ = args.Contains("--self-test");   // the only mode
int passed = 0, total = 0;
void Check(string name, bool ok)
{
    total++;
    if (ok) passed++;
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
}

Console.WriteLine("=== Ch18.InterfaceTests (offline) ===\n");
await using TestHost host = await TestHost.StartAsync(new ToolScriptChatClient());
HttpClient http = host.Http;
string alice = host.Token("contoso", "E-1001"), bob = host.Token("contoso", "E-2002");
string rita = host.Token("contoso", "rita"), ana = host.Token("contoso", "ana"), fabrikamAlice = host.Token("fabrikam", "E-1001");
OperationLog log = host.App.Services.GetRequiredService<OperationLog>();
ApprovalDesk desk = host.App.Services.GetRequiredService<ApprovalDesk>();

Console.WriteLine("-- 1. Replay: disconnect, the operation moves on, resume from the cursor");
ContosoClient ui = new(http, alice);
await ui.RunAsync("/agui/assistant", "t-replay", "Please submit my taxi receipt of 120 EUR");
StatusView client = new();
foreach (StatusEvent e in await Read(http, alice, "t-replay", 0, e => e.State == OperationState.AwaitingApproval)) client.Apply(e);
long dropped = client.Cursor;   // the network drops here
Guid replayId = PendingOn("t-replay");
ReviewView rv = (await View(rita, replayId))!;
await Decide(rita, replayId, new DecisionRequest(rv.Version, rv.Digest, "approve"));
List<StatusEvent> resumed = await Read(http, alice, "t-replay", dropped, _ => false);
resumed.ForEach(client.Apply);
Check($"resumed from cursor {dropped}: {resumed.Count} event(s), {string.Join(",", resumed.Select(e => $"{e.State}"))}; DONE {client.ShowsDone}",
    resumed.Count == 1 && client.ShowsDone && client.State == OperationState.Completed);
Check($"every event id applied exactly once: {client.AppliedIds.Count} ids for cursors 1..{log.Cursor("t-replay")}",
    client.AppliedIds.Count == log.Cursor("t-replay") && client.Discarded == 0);
(await Read(http, alice, "t-replay", 0, _ => false)).ForEach(client.Apply);
Check($"a full replay from 0 onto the same view: {client.Discarded} discarded, the view unchanged", client.Discarded == log.Cursor("t-replay"));

Console.WriteLine("\n-- 2. Stale binding: the arguments change under an open review screen");
await ui.RunAsync("/agui/assistant", "t-stale", "Please submit my hotel receipt of 120 EUR");
Guid staleId = PendingOn("t-stale");
ReviewView seen = (await View(rita, staleId))!;
ClaimOperations.Claim claim = host.Claims.Find(staleId)!;
long accountVersion = desk.Commits.LoadAccount("contoso", "E-1001")!.Version;
desk.Revise(staleId, desk.Arguments(staleId)! with { Amount = 130m, ExpectedVersion = accountVersion }, out int v2);
Check($"the application revises the claim to 130 EUR (version {v2}); rita posts the screen she opened (version {seen.Version}): {await Decide(rita, staleId, new DecisionRequest(seen.Version, seen.Digest, "approve"))}",
    v2 == 2 && (await Decide(rita, staleId, new DecisionRequest(seen.Version, seen.Digest, "approve"))) == "409 stale_version");
ReviewView fresh = (await View(rita, staleId))!;
Check($"version 2's number with version 1's digest: {await Decide(rita, staleId, new DecisionRequest(2, seen.Digest, "approve"))}",
    await Decide(rita, staleId, new DecisionRequest(2, seen.Digest, "approve")) == "409 digest_mismatch");
Check($"rita reloads ({fresh.Amount} EUR, version {fresh.Version}) and approves: {await Decide(rita, staleId, new DecisionRequest(fresh.Version, fresh.Digest, "approve"))}",
    (await Snapshot(alice, "t-stale"))?.State == OperationState.Completed);
Check($"the commit paid the reviewed amount: balance {desk.Commits.LoadAccount("contoso", "E-1001")!.ReimbursableBalance} (500 - 120 - 130)",
    desk.Commits.LoadAccount("contoso", "E-1001")!.ReimbursableBalance == 250m && claim.Thread == "t-stale");

Console.WriteLine("\n-- 3. Double click: two approve POSTs at once");
await ui.RunAsync("/agui/assistant", "t-double", "Please submit my train receipt of 60 EUR");
Guid doubleId = PendingOn("t-double");
ReviewView dv = (await View(rita, doubleId))!;
long commitsBefore = desk.Commits.Count("operations");
Task<string> click1 = Task.Run(() => Decide(rita, doubleId, new DecisionRequest(dv.Version, dv.Digest, "approve")));
Task<string> click2 = Task.Run(() => Decide(rita, doubleId, new DecisionRequest(dv.Version, dv.Digest, "approve")));
string[] clicks = [await click1, await click2];
int completedEvents = (await Read(http, alice, "t-double", 0, _ => false)).Count(e => e.State == OperationState.Completed);
Check($"clicks (Task.Run): {clicks[0]} / {clicks[1]}", clicks.Count(c => c == "200 Approved") == 1 && clicks.Count(c => c == "200 Approved replayed") == 1);
Check($"one stored decision ({desk.DecisionCount(doubleId)}), one commit (+{desk.Commits.Count("operations") - commitsBefore}), one Completed event ({completedEvents})",
    desk.DecisionCount(doubleId) == 1 && desk.Commits.Count("operations") - commitsBefore == 1 && completedEvents == 1);

Console.WriteLine("\n-- 4. Slow client: bounded, dropped, resynced");
await ui.RunAsync("/agui/assistant", "t-slow", "Please submit my lunch receipt of 80 EUR");
Subscription stalled = log.Subscribe("t-slow", log.Cursor("t-slow"));
string slowRun = log.Snapshot("t-slow").RunId;
StatusView slowView = new();
slowView.Apply(log.Snapshot("t-slow"));
long slowFrom = slowView.Cursor;
for (int i = 0; i < 1000; i++) log.Tool("t-slow", slowRun, "receipt_ocr", $"page {i}");
Check($"1000 events to a client that reads nothing: peak {stalled.PeakQueued} of {log.ClientQueue}, dropped {stalled.Overflowed}",
    stalled.PeakQueued <= log.ClientQueue && stalled.Overflowed);
List<StatusEvent> back = await Read(http, alice, "t-slow", slowFrom, _ => true);
slowView.Apply(back[0]);
Check($"it reconnects from cursor {slowFrom}: the window has moved on, so the server sends a {back[0].Type} at {back[0].Cursor}; state {slowView.State}",
    back[0].Type == EventTypes.Snapshot && slowView.State == OperationState.AwaitingApproval && !slowView.NeedsResync);

Console.WriteLine("\n-- 5. A reviewer revoked mid-review");
Guid slowId = PendingOn("t-slow");
ReviewView anaView = (await View(ana, slowId))!;
host.Rights.Revoke("contoso", "ana", Roles.FinanceReviewer);
(int anaAgent, _) = await AguiWire.RunAsync(http, "/agui/review", ana, AguiWire.Body("t-ana", "r1", ("m1", "review")));
Check($"after the revocation: review agent {anaAgent}, review screen {(await View(ana, slowId) is null ? 404 : 200)}, POST {await Decide(ana, slowId, new DecisionRequest(anaView.Version, anaView.Digest, "approve"))}",
    anaAgent == 403 && await View(ana, slowId) is null
    && await Decide(ana, slowId, new DecisionRequest(anaView.Version, anaView.Digest, "approve")) == "403 reviewer_not_authorized");
Check($"the claim is still {(await Snapshot(alice, "t-slow"))?.State}", (await Snapshot(alice, "t-slow"))?.State == OperationState.AwaitingApproval);

Console.WriteLine("\n-- 6. A valid id under another identity");
(int bobAgui, _) = await AguiWire.RunAsync(http, "/agui/assistant", bob, AguiWire.Body("t-replay", "r9", ("m9", "hello")));
int bobEvents = await Status(bob, "/conversations/t-replay/events"), bobSnap = await Status(bob, "/conversations/t-replay/snapshot");
int bobReview = await Status(bob, $"/reviews/{slowId}");
Check($"bob with alice's thread id: AG-UI {bobAgui}, events {bobEvents}, snapshot {bobSnap}; with her proposal id: review {bobReview}",
    bobAgui == 404 && bobEvents == 404 && bobSnap == 404 && bobReview == 404);
int fabAgui = (await AguiWire.RunAsync(http, "/agui/assistant", fabrikamAlice, AguiWire.Body("t-replay", "r9", ("m9", "hello")))).Status;
int fabSnap = await Status(fabrikamAlice, "/conversations/t-replay/snapshot");
Check($"fabrikam/E-1001 (alice's subject, another tenant): AG-UI {fabAgui}, snapshot {fabSnap}", fabAgui == 403 && fabSnap == 404);
Check($"no token: snapshot {await Status(null, "/conversations/t-replay/snapshot")}", await Status(null, "/conversations/t-replay/snapshot") == 401);

Console.WriteLine($"\nRESULT {passed}/{total}");
return passed == total ? 0 : 1;

Guid PendingOn(string thread) => host.Claims.Pending().First(p => host.Claims.Find(p)!.Thread == thread);

async Task<int> Status(string? token, string path)
{
    using HttpRequestMessage r = new(HttpMethod.Get, path);
    if (token is not null) r.Headers.Authorization = new("Bearer", token);
    using HttpResponseMessage response = await http.SendAsync(r, HttpCompletionOption.ResponseHeadersRead);
    return (int)response.StatusCode;
}

async Task<ReviewView?> View(string token, Guid id)
{
    using HttpRequestMessage r = new(HttpMethod.Get, $"/reviews/{id}");
    r.Headers.Authorization = new("Bearer", token);
    using HttpResponseMessage response = await http.SendAsync(r);
    return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<ReviewView>() : null;
}

async Task<string> Decide(string token, Guid id, DecisionRequest request)
{
    using HttpRequestMessage r = new(HttpMethod.Post, $"/reviews/{id}/decision") { Content = JsonContent.Create(request) };
    r.Headers.Authorization = new("Bearer", token);
    using HttpResponseMessage response = await http.SendAsync(r);
    using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    return $"{(int)response.StatusCode} {doc.RootElement.GetProperty("result").GetString()}" +
           (doc.RootElement.GetProperty("replayed").GetBoolean() ? " replayed" : "");
}

async Task<StatusEvent?> Snapshot(string token, string thread)
{
    using HttpRequestMessage r = new(HttpMethod.Get, $"/conversations/{thread}/snapshot");
    r.Headers.Authorization = new("Bearer", token);
    using HttpResponseMessage response = await http.SendAsync(r);
    return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<StatusEvent>(JsonSerializerOptions.Web) : null;
}

static async Task<List<StatusEvent>> Read(HttpClient http, string token, string thread, long after, Func<StatusEvent, bool> stop)
{
    List<StatusEvent> got = [];
    using CancellationTokenSource cts = new(TimeSpan.FromSeconds(2));
    try
    {
        await foreach (StatusEvent e in StatusStreamClient.ReadAsync(http, $"/conversations/{thread}/events", after, token, cts.Token))
        {
            got.Add(e);
            if (stop(e)) break;
        }
    }
    catch (OperationCanceledException) { }
    return got;
}
