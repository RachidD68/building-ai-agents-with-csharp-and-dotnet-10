using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Ch18.AguiServer;

/// <summary>
/// A stand-in model with no network, for the self-tests. "submit ... 120"
/// calls submit_claim, "my claims" calls list_my_claims, "review" calls
/// pending_reviews; after a tool result it streams that result back word by
/// word. Anything else: which user messages reached it, so a test sees
/// whose history a thread holds.
/// </summary>
public sealed partial class ToolScriptChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException("streaming only");

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        List<ChatMessage> all = [.. messages];
        int lastUser = all.FindLastIndex(m => m.Role == ChatRole.User);
        string ask = lastUser >= 0 ? all[lastUser].Text : "";
        FunctionResultContent? result = all.Skip(lastUser + 1).SelectMany(m => m.Contents).OfType<FunctionResultContent>().LastOrDefault();
        string id = "msg_" + Guid.NewGuid().ToString("N")[..8];
        if (result is null && Tool(ask) is { } call)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, [call]) { MessageId = id };
            yield break;
        }
        string text = result is not null
            ? $"The application says: {result.Result}."
            : $"I see {all.Count(m => m.Role == ChatRole.User)} user message(s); the first: '{all.FirstOrDefault(m => m.Role == ChatRole.User)?.Text}'.";
        foreach (string word in text.Split(' '))
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, word + " ") { MessageId = id };
        }
    }

    private static FunctionCallContent? Tool(string ask)
    {
        string callId = "call_" + Guid.NewGuid().ToString("N")[..8];
        if (ask.Contains("submit", StringComparison.OrdinalIgnoreCase) && Amount().Match(ask) is { Success: true } m)
            return new FunctionCallContent(callId, "submit_claim", new Dictionary<string, object?>
            {
                ["amount"] = decimal.Parse(m.Value, CultureInfo.InvariantCulture), ["currency"] = "EUR",
            });
        if (ask.Contains("my claims", StringComparison.OrdinalIgnoreCase)) return new FunctionCallContent(callId, "list_my_claims");
        if (ask.Contains("review", StringComparison.OrdinalIgnoreCase)) return new FunctionCallContent(callId, "pending_reviews");
        return null;
    }

    [GeneratedRegex(@"\d+(\.\d+)?")]
    private static partial Regex Amount();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

/// <summary>An in-memory AG-UI host with tokens minted in process: the tests' server.</summary>
public sealed class TestHost : IAsyncDisposable
{
    private const string Issuer = "contoso-ch18", Audience = "contoso-expense-ui";
    private readonly byte[] _key;

    private TestHost(WebApplication app, string db, byte[] key) => (App, DatabasePath, _key) = (app, db, key);

    public WebApplication App { get; }
    public string DatabasePath { get; }
    public HttpClient Http => App.GetTestClient();
    public Rights Rights => App.Services.GetRequiredService<Rights>();
    public ClaimOperations Claims => App.Services.GetRequiredService<ClaimOperations>();

    public static async Task<TestHost> StartAsync(IChatClient model, bool guardThreads = true, Action<string>? agentBuilt = null,
        bool readThreadIdLikeMaf = true, bool isolateSessions = true, bool registerIsolationKey = true)
    {
        string db = Path.Combine(Path.GetTempPath(), $"ch18-agui-{Guid.NewGuid():N}.db");
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        byte[] key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Schemes:Bearer:ValidIssuer"] = Issuer,
            ["Authentication:Schemes:Bearer:ValidAudiences:0"] = Audience,
            ["Authentication:Schemes:Bearer:SigningKeys:0:Issuer"] = Issuer,
            ["Authentication:Schemes:Bearer:SigningKeys:0:Value"] = Convert.ToBase64String(key),
        });
        AguiHost.Configure(builder, new AguiHostOptions
        {
            ChatClient = model, GuardThreads = guardThreads, ReadThreadIdLikeMaf = readThreadIdLikeMaf, IsolateSessions = isolateSessions,
            RegisterIsolationKey = registerIsolationKey, DatabasePath = db, AgentBuilt = agentBuilt,
        });
        WebApplication app = builder.Build();
        AguiHost.Map(app);
        await app.StartAsync();
        return new TestHost(app, db, key);
    }

    public string Token(string tenant, string subject) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Issuer = Issuer, Audience = Audience,
        Claims = new Dictionary<string, object> { ["sub"] = subject, ["tid"] = tenant },
        Expires = DateTime.UtcNow.AddMinutes(10),
        SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(_key), SecurityAlgorithms.HmacSha256),
    });

    public async ValueTask DisposeAsync()
    {
        await App.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(DatabasePath); } catch (IOException) { }
    }
}

/// <summary>The AG-UI request body (RunAgentInput) and a raw reader of the event stream, for tests.</summary>
public static class AguiWire
{
    public static string Body(string thread, string run, params (string Id, string Text)[] userMessages) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            threadId = thread, runId = run,
            messages = userMessages.Select(m => new { id = m.Id, role = "user", content = m.Text }),
            tools = Array.Empty<object>(), context = Array.Empty<object>(), state = new { }, forwardedProps = new { },
        });

    /// <summary>The same body with one more root property at the end (a JSON parser that keeps the last duplicate sees it).</summary>
    public static string WithProperty(string body, string name, string value) =>
        body[..^1] + "," + System.Text.Json.JsonSerializer.Serialize(name) + ":" + System.Text.Json.JsonSerializer.Serialize(value) + "}";

    /// <summary>POSTs one run; returns the status code and the events (type and the raw JSON).</summary>
    public static async Task<(int Status, List<System.Text.Json.JsonElement> Events)> RunAsync(HttpClient http, string path,
        string? token, string body, CancellationToken ct = default)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, path)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        if (token is not null) request.Headers.Authorization = new("Bearer", token);
        using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        List<System.Text.Json.JsonElement> events = [];
        if (!response.IsSuccessStatusCode) return ((int)response.StatusCode, events);
        await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
        await foreach (System.Net.ServerSentEvents.SseItem<System.Text.Json.JsonElement> item in
            System.Net.ServerSentEvents.SseParser.Create(stream, (_, data) =>
                System.Text.Json.JsonDocument.Parse(data.ToArray()).RootElement.Clone()).EnumerateAsync(ct))
            events.Add(item.Data);
        return ((int)response.StatusCode, events);
    }

    public static string Text(IEnumerable<System.Text.Json.JsonElement> events) => string.Concat(events
        .Where(e => e.GetProperty("type").GetString() == "TEXT_MESSAGE_CONTENT")
        .Select(e => e.GetProperty("delta").GetString()));
}
