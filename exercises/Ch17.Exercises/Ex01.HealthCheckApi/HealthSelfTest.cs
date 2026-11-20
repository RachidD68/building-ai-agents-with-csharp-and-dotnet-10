using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;

namespace Ex01.HealthCheckApi;

/// <summary>dotnet run -- --self-test: the API on a TestServer with a counting fake model.</summary>
internal static class HealthSelfTest
{
    private static int _passed, _total;

    public static async Task<int> RunAsync()
    {
        Console.WriteLine("=== Ex01.HealthCheckApi: self-test (TestServer, counting fake model, offline) ===\n");
        CountingChatClient model = new();
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        await using WebApplication app = HealthApi.Build(builder, model);
        await app.StartAsync();
        HttpClient http = app.GetTestClient();
        SessionStore store = app.Services.GetRequiredService<SessionStore>();

        int ok = 0;
        for (int i = 0; i < 100; i++)
            if ((await http.GetAsync("/health/live")).IsSuccessStatusCode) ok++;
        Check($"100 liveness probes: {ok} x 200, {model.Calls} model calls", ok == 100 && model.Calls == 0);

        (int status, string body) ready = await GetAsync(http, "/health/ready");
        Check($"readiness with the store up: {ready.status} {ready.body}", ready.status == 200 && model.Calls == 0);

        store.Down = true;
        ready = await GetAsync(http, "/health/ready");
        Check($"store down, readiness: {ready.status} {ready.body}",
            ready.status == 503 && ready.body.Contains("\"session-store\",\"status\":\"Unhealthy\"", StringComparison.Ordinal));
        Check("store down, liveness still 200 (a dependency outage is not a dead process)",
            (await http.GetAsync("/health/live")).IsSuccessStatusCode);
        Check($"no probe reached the model: {model.Calls} calls", model.Calls == 0);
        store.Down = false;

        HttpResponseMessage chat = await http.PostAsJsonAsync("/chat", new ChatRequest("hello"));
        Check($"the counter works: POST /chat makes {model.Calls} model call", chat.IsSuccessStatusCode && model.Calls == 1);

        await app.StopAsync();
        Console.WriteLine($"\nRESULT {_passed}/{_total}");
        return _passed == _total ? 0 : 1;
    }

    private static async Task<(int, string)> GetAsync(HttpClient http, string path)
    {
        using HttpResponseMessage response = await http.GetAsync(path);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        string compact = JsonSerializer.Serialize(doc.RootElement.GetProperty("checks"));
        return ((int)response.StatusCode, compact);
    }

    private static void Check(string name, bool ok)
    {
        _total++;
        if (ok) _passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    }

    private sealed class CountingChatClient : IChatClient
    {
        private int _calls;
        public int Calls => _calls;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "OK")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (ChatResponseUpdate update in response.ToChatResponseUpdates()) yield return update;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
