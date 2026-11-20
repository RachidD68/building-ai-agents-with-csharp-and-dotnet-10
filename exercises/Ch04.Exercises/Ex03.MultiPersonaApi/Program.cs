// ============================================================================
// Exercise 03: Multi-Persona API (Challenge)
// ----------------------------------------------------------------------------
// One API, several agents registered as keyed services; the request names the
// persona. An unknown persona is a 404 and an empty message a 400, both before
// any model call; a provider error is a 502. The stream ends with an explicit
// 'done' event that carries the run's terminal status.
//
//   POST /chat          { "persona": "pirate", "message": "..." }
//   GET  /chat/stream   ?persona=pirate&message=...
//   GET  /personas
//
//   dotnet run                    against the configured model (Ollama llama3.2)
//   dotnet run -- --self-test     the real app on a loopback port with a fake
//                                 model, no outside network; RESULT n/n
// ============================================================================

using System.Net;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AgenticAI.Common.Extensions;
using AgenticAI.Testing;
using Ch04.FirstAgent;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.AI;

if (args.Contains("--self-test")) return await SelfTest.RunAsync();
Api.Build(args).Run();
return 0;

internal sealed record Persona(string Key, string Name, string Instructions);
internal sealed record PersonaRequest(string? Persona, string? Message);
internal sealed record PersonaReply(string Persona, string Agent, string Message, string Status);

internal static class Api
{
    public static readonly Persona[] Personas =
    [
        new("pirate", "CaptainCodeBeard", "You are a pirate who explains programming. Three sentences at most."),
        new("auditor", "StrictAuditor", "You are a strict code auditor. Name risks first. Three sentences at most."),
    ];

    public static WebApplication Build(string[] args, IChatClient? model = null, Action<WebApplicationBuilder>? configure = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        if (model is null) builder.Services.AddLlmChatClient(builder.Configuration);
        else builder.Services.AddSingleton(model);
        foreach (Persona p in Personas)
        {
            builder.Services.AddKeyedSingleton(p.Key, (sp, _) =>
                new ChatClientAgent(sp.GetRequiredService<IChatClient>(), instructions: p.Instructions, name: p.Name));
        }
        configure?.Invoke(builder);

        WebApplication app = builder.Build();
        app.MapGet("/personas", () => Personas.Select(p => new { persona = p.Key, name = p.Name }));
        app.MapPost("/chat", async (PersonaRequest request, IServiceProvider sp, CancellationToken aborted) =>
        {
            if (Check(request.Persona, request.Message, sp, out ChatClientAgent? agent) is { } problem) return problem;
            RunOutcome outcome = await AgentRuns.RunAsync(
                () => agent!.RunAsync(request.Message!, cancellationToken: aborted), aborted);
            return outcome.Status is RunStatus.Completed or RunStatus.Truncated
                ? Results.Ok(new PersonaReply(request.Persona!, agent!.Name!, outcome.Text, Wire(outcome.Status)))
                : outcome.Status == RunStatus.Cancelled ? Results.StatusCode(499)
                : Results.Problem($"The model call failed ({outcome.Error?.GetType().Name}).",
                    statusCode: StatusCodes.Status502BadGateway, title: "provider_error");
        });
        app.MapGet("/chat/stream", (string? persona, string? message, IServiceProvider sp, HttpContext http) =>
            Check(persona, message, sp, out ChatClientAgent? agent) is { } problem
                ? problem
                : TypedResults.ServerSentEvents(Events(agent!, message!, http.RequestAborted)));
        return app;
    }

    /// <summary>Validation and routing, both before any model call.</summary>
    private static IResult? Check(string? persona, string? message, IServiceProvider sp, out ChatClientAgent? agent)
    {
        agent = persona is null ? null : sp.GetKeyedService<ChatClientAgent>(persona);
        if (agent is null)
            return Results.Problem($"Unknown persona '{persona}'.", statusCode: StatusCodes.Status404NotFound, title: "unknown_persona");
        if (string.IsNullOrWhiteSpace(message))
            return Results.Problem("A message is required.", statusCode: StatusCodes.Status400BadRequest, title: "validation_error");
        return null;
    }

    private static async IAsyncEnumerable<SseItem<string>> Events(
        ChatClientAgent agent, string message, [EnumeratorCancellation] CancellationToken aborted)
    {
        RunTracker tracker = new(aborted);
        await using (IAsyncEnumerator<AgentResponseUpdate> updates =
                     agent.RunStreamingAsync(message, cancellationToken: aborted).GetAsyncEnumerator())
        {
            while (await tracker.NextAsync(updates) is { } update)
            {
                if (update.Text is { Length: > 0 } text) yield return new SseItem<string>(text, "delta");
            }
        }
        yield return new SseItem<string>(JsonSerializer.Serialize(new { status = Wire(tracker.Outcome.Status) }), "done");
    }

    private static string Wire(RunStatus status) => status.ToString().ToLowerInvariant();
}

internal static class SelfTest
{
    public static async Task<int> RunAsync()
    {
        Console.WriteLine("=== Multi-Persona API (self-test) ===");
        int passed = 0, total = 0;
        void Expect(string name, bool ok) { total++; if (ok) passed++; Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}"); }

        FakeChatClient model = new();
        await using WebApplication app = Api.Build([], model,
            b => { b.WebHost.UseUrls("http://127.0.0.1:0"); b.Logging.ClearProviders(); });
        await app.StartAsync();
        using HttpClient http = new()
        {
            BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()),
        };

        HttpResponseMessage r = await http.PostAsJsonAsync("/chat", new PersonaRequest("wizard", "Hi"));
        Expect("an unknown persona is 404 unknown_persona and no model call",
            r.StatusCode == HttpStatusCode.NotFound && await Title(r) == "unknown_persona" && model.Calls.Count == 0);
        r = await http.PostAsJsonAsync("/chat", new PersonaRequest("pirate", " "));
        Expect("an empty message is 400 validation_error and no model call",
            r.StatusCode == HttpStatusCode.BadRequest && await Title(r) == "validation_error" && model.Calls.Count == 0);

        model.Then(FakeReply.Text("Arrr, a function that calls itself."));
        r = await http.PostAsJsonAsync("/chat", new PersonaRequest("auditor", "What is recursion?"));
        PersonaReply? reply = await r.Content.ReadFromJsonAsync<PersonaReply>();
        Expect("the request's persona picks the agent whose instructions reach the model",
            reply is { Agent: "StrictAuditor", Status: "completed" }
            && model.Calls[^1].Options?.Instructions == Api.Personas[1].Instructions);

        model.Then(FakeReply.Throw(new HttpRequestException("model server returned 503")));
        r = await http.PostAsJsonAsync("/chat", new PersonaRequest("pirate", "What is recursion?"));
        Expect("a provider error is 502 provider_error, distinct from the 400",
            r.StatusCode == HttpStatusCode.BadGateway && await Title(r) == "provider_error");

        model.Then(FakeReply.Stream(ChatFinishReason.Stop, "Arrr, ", "recursion."),
                   FakeReply.Throw(new HttpRequestException("connection reset"), "Arrr, "));
        string ok = await http.GetStringAsync("/chat/stream?persona=pirate&message=recursion");
        string broken = await http.GetStringAsync("/chat/stream?persona=pirate&message=recursion");
        Console.WriteLine($"   last lines: {Last(ok)} / {Last(broken)}");
        Expect("the stream ends with done/completed", Last(ok) == """data: {"status":"completed"}""");
        Expect("a failed stream still ends with done/failed", Last(broken) == """data: {"status":"failed"}""");
        r = await http.GetAsync("/chat/stream?persona=wizard&message=hi");
        Expect("an unknown persona on the stream route is 404 before any event", r.StatusCode == HttpStatusCode.NotFound);

        await app.StopAsync();
        Console.WriteLine($"\nRESULT {passed}/{total}");
        return passed == total ? 0 : 1;
    }

    private static async Task<string?> Title(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.TryGetProperty("title", out JsonElement t)
            ? t.GetString() : null;

    private static string Last(string sse) => sse.Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1].TrimEnd('\r');
}
