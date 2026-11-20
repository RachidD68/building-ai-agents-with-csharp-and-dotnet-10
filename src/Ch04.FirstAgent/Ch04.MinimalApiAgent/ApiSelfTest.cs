using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgenticAI.Testing;
using Ch04.FirstAgent;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.AI;

namespace Ch04.MinimalApiAgent;

/// <summary>
/// The real app, in process, on a loopback port, with a FakeChatClient in place
/// of the provider. No model, no outside network.
/// </summary>
internal static class ApiSelfTest
{
    private static int s_passed, s_total;

    public static async Task<int> RunAsync()
    {
        Console.WriteLine("=== Contoso expense help: HTTP endpoints (self-test) ===");
        FakeChatClient model = new();
        await using WebApplication app = AgentApi.Build([], model,
            new AgentApiLimits(MaxMessageLength: 200, TimeBudgetSeconds: 1),
            b => { b.WebHost.UseUrls("http://127.0.0.1:0"); b.Logging.ClearProviders(); });
        await app.StartAsync();
        string address = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.First();
        using HttpClient http = new() { BaseAddress = new Uri(address) };
        RunLog log = app.Services.GetRequiredService<RunLog>();

        Console.WriteLine("\n-- POST /chat --");
        HttpResponseMessage r = await Post(http, "");
        Expect("an empty message is 400 validation_error and no model call",
            r.StatusCode == HttpStatusCode.BadRequest && await Title(r) == "validation_error" && model.Calls.Count == 0);
        r = await Post(http, new string('x', 201));
        Expect("a message over the size budget is 400 and no model call",
            r.StatusCode == HttpStatusCode.BadRequest && model.Calls.Count == 0);

        model.Then(FakeReply.Text("Yes, under 50.00 EUR."), FakeReply.Text("A finance reviewer."));
        r = await Post(http, "Is a 42 EUR taxi approved automatically?");
        ChatReply? reply = await r.Content.ReadFromJsonAsync<ChatReply>();
        await log.NextAsync(default);
        Expect("a good request is 200 completed, labelled single-turn",
            r.StatusCode == HttpStatusCode.OK && reply is { Status: "completed", SingleTurn: true }
            && model.Calls[0].Options?.Instructions == ContosoPolicy.Instructions);
        await Post(http, "Who approves it?");
        await log.NextAsync(default);
        Expect("the next request carries no history from the first",
            model.Calls[1].Messages.Count(m => m.Role != ChatRole.System) == 1);

        model.Then(FakeReply.Text("Claims up to", ChatFinishReason.Length));
        r = await Post(http, "Explain the whole policy.");
        reply = await r.Content.ReadFromJsonAsync<ChatReply>();
        await log.NextAsync(default);
        Expect("a cut-off answer is 200 with status truncated", reply?.Status == "truncated");

        model.Then(FakeReply.Throw(new HttpRequestException("model server returned 503")));
        r = await Post(http, "Is parking reimbursed?");
        await log.NextAsync(default);
        Expect("a provider error is 502 provider_error, not a validation error",
            r.StatusCode == HttpStatusCode.BadGateway && await Title(r) == "provider_error");

        model.Then(FakeReply.Hang());
        r = await Post(http, "Is parking reimbursed?");
        RunOutcome slow = await log.NextAsync(default);
        Expect("a model that outlives the time budget is 504 time_budget",
            r.StatusCode == HttpStatusCode.GatewayTimeout && await Title(r) == "time_budget" && slow.Status == RunStatus.Failed);

        Console.WriteLine("\n-- GET /chat/stream --");
        int before = model.Calls.Count;
        r = await http.GetAsync("/chat/stream");
        Expect("a missing message is 400 before any event, and no model call",
            r.StatusCode == HttpStatusCode.BadRequest && r.Content.Headers.ContentType?.MediaType != "text/event-stream"
            && model.Calls.Count == before);

        model.Then(FakeReply.Stream(ChatFinishReason.Stop, "Claims up to ", "50.00 EUR ", "are automatic."));
        List<(string Event, string Data)> events = await Events(http, "Which claims are automatic?");
        await log.NextAsync(default);
        Print(events);
        Expect("deltas carry the text and the last event is done/completed",
            string.Concat(events.Where(e => e.Event == "delta").Select(e => e.Data)) == "Claims up to 50.00 EUR are automatic."
            && events[^1] is ("done", var d) && Status(d) == "completed");

        model.Then(FakeReply.Throw(new HttpRequestException("connection reset"), "Claims up to "));
        events = await Events(http, "Which claims are automatic?");
        await log.NextAsync(default);
        Print(events);
        Expect("an error mid-stream still ends with done/failed",
            events[^1] is ("done", var f) && Status(f) == "failed" && f.Contains("provider_error"));

        model.Then(FakeReply.Hang("Claims up to "));
        // No drain on dispose: closing the response closes the connection at once.
        using HttpClient leaving = new(new SocketsHttpHandler { MaxResponseDrainSize = 0 }) { BaseAddress = http.BaseAddress };
        using (HttpResponseMessage open = await leaving.GetAsync(
                   "/chat/stream?message=Explain%20the%20policy", HttpCompletionOption.ResponseHeadersRead))
        using (StreamReader reader = new(await open.Content.ReadAsStreamAsync()))
        {
            while (await reader.ReadLineAsync() is { } line && !line.StartsWith("data:")) { }
        }                                                  // the client disconnects after the first delta
        using CancellationTokenSource wait = new(TimeSpan.FromSeconds(10));
        RunOutcome gone = await log.NextAsync(wait.Token);
        Console.WriteLine($"   server log: the run ended {gone.Status}");
        Expect("a client that disconnects mid-stream is logged Cancelled and the model call is stopped",
            gone.Status == RunStatus.Cancelled && model.Calls[^1].SawCancellation);

        await app.StopAsync();
        Console.WriteLine($"\nRESULT {s_passed}/{s_total}");
        return s_passed == s_total ? 0 : 1;
    }

    private static Task<HttpResponseMessage> Post(HttpClient http, string message) =>
        http.PostAsJsonAsync("/chat", new ChatRequest(message));

    private static async Task<string?> Title(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.TryGetProperty("title", out JsonElement t)
            ? t.GetString() : null;

    private static string? Status(string json) => JsonDocument.Parse(json).RootElement.GetProperty("status").GetString();

    /// <summary>Reads a whole event stream as (event, data) pairs.</summary>
    private static async Task<List<(string, string)>> Events(HttpClient http, string message)
    {
        string body = await http.GetStringAsync($"/chat/stream?message={Uri.EscapeDataString(message)}");
        List<(string, string)> events = [];
        string name = "message";
        foreach (string line in body.Split('\n'))
        {
            if (line.StartsWith("event: ")) name = line[7..];
            else if (line.StartsWith("data: ")) events.Add((name, line[6..]));
        }
        return events;
    }

    private static void Print(List<(string Event, string Data)> events) =>
        Console.WriteLine("   " + string.Join(" | ", events.Select(e => $"{e.Event}: {e.Data}")));

    private static void Expect(string name, bool ok)
    {
        s_total++;
        if (ok) s_passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    }
}
