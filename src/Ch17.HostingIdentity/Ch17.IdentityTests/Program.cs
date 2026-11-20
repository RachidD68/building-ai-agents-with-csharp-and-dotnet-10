// ============================================================================
// Ch17.IdentityTests: the hosting chapter's two-user proof, offline.
// Ch17.AspNetCoreHost's real composition (AgentHost.Configure + Map) runs on
// an in-memory TestServer. Tokens are minted here and validated by the real
// JWT bearer handler; the model is HistoryEchoChatClient, which answers with
// every user line it was sent. Every request arrives from 10.0.0.1.
//
//   dotnet run                      (or -- --self-test; same run)
// ============================================================================

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Ch17.AspNetCoreHost;
using Ch17.IdentityTests;
using Microsoft.AspNetCore.TestHost;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

_ = args.Contains("--self-test");    // the CI discovers harnesses by this flag; both forms run everything
Console.WriteLine("=== Ch17.IdentityTests (TestServer, minted tokens, fake model, offline) ===");
string root = Path.Combine(Path.GetTempPath(), "AgenticAI", "Ch17.IdentityTests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int passed = 0, total = 0;
void Check(string name, bool ok)
{
    total++;
    if (ok) passed++;
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
}

Tokens tokens = new();
string alice = tokens.Mint("contoso", "alice"), bob = tokens.Mint("contoso", "bob");
string fabrikamAlice = tokens.Mint("fabrikam", "alice");

try
{
    Console.WriteLine("\n-- 1. Authentication: the token is validated before any route runs");
    await using (TestHost h = await TestHost.StartAsync(root, tokens.Key, permits: 100))
    {
        Check("no token: 401", await h.Status(null, HttpMethod.Post, "/conversations") == 401);
        Check("wrong audience: 401",
            await h.Status(tokens.Mint("contoso", "alice", audience: "another-api"), HttpMethod.Post, "/conversations") == 401);
        Check("expired: 401",
            await h.Status(tokens.Mint("contoso", "alice", expires: DateTime.UtcNow.AddMinutes(-10)), HttpMethod.Post, "/conversations") == 401);
        Check("signed with another key: 401",
            await h.Status(new Tokens().Mint("contoso", "alice"), HttpMethod.Post, "/conversations") == 401);
        Check("valid token without \"tid\": 403 (no identity key, no conversation)",
            await h.Status(tokens.Mint(null, "alice"), HttpMethod.Post, "/conversations") == 403);
    }
    await using (TestHost h = await TestHost.StartAsync(root, tokens.Key, permits: 100, configureAudience: false))
    {
        Check("no audience configured: a valid token is refused (401), not accepted for any audience",
            await h.Status(alice, HttpMethod.Post, "/conversations") == 401);
    }

    Console.WriteLine("\n-- 2. Conversations: the id round trip, and who can load it");
    await using (TestHost h = await TestHost.StartAsync(root, tokens.Key, permits: 100))
    {
        string id = await h.NewConversation(alice);
        await h.Turn(alice, id, "my claim is CLM-7");
        (int status, string text) second = await h.Turn(alice, id, "which claim did I mention?");
        Check($"A resumes A's second turn: \"{second.text}\"",
            second.status == 200 && second.text == "user turns seen: my claim is CLM-7 | which claim did I mention?");

        await h.Ask(alice, "my claim is CLM-7");
        (int status, string text) single = await h.Ask(alice, "which claim did I mention?");
        Check($"DEFECT REPRODUCED: the single-turn route forgets the first request: \"{single.text}\"",
            single.text == "user turns seen: which claim did I mention?");

        Check("B (same tenant) posting to A's conversation id: 404", (await h.Turn(bob, id, "B was here")).Status == 404);
        Check("fabrikam/alice (same \"sub\", other tenant): 404", (await h.Turn(fabrikamAlice, id, "F was here")).Status == 404);
        Check("B's DELETE of A's id is a no-op: 204", await h.Status(bob, HttpMethod.Delete, $"/conversations/{id}") == 204);
        (int status, string text) third = await h.Turn(alice, id, "still mine?");
        Check("A's conversation is intact and holds no B or F text",
            third.status == 200 && !third.text.Contains("was here", StringComparison.Ordinal) && third.text.Contains("CLM-7", StringComparison.Ordinal));
    }

    Console.WriteLine("\n-- 3. Two concurrent turns on one conversation (Task.Run, both inside the model together)");
    await using (TestHost h = await TestHost.StartAsync(root, tokens.Key, permits: 100))
    {
        string id = await h.NewConversation(alice);
        await h.Turn(alice, id, "first");
        Task<(int, string)> left = Task.Run(() => h.Turn(alice, id, "left [gate]"));
        Task<(int, string)> right = Task.Run(() => h.Turn(alice, id, "right [gate]"));
        (int ls, string _) = await left;
        (int rs, string _) = await right;
        Check($"one turn saved, one refused: {ls} and {rs}", (ls, rs) is (200, 409) or (409, 200));
        string winner = ls == 200 ? "left" : "right", loser = ls == 200 ? "right" : "left";
        (int _, string after) = await h.Turn(alice, id, "next");
        Check($"the refused turn overwrote nothing: \"{after}\"",
            after.Contains($"{winner} [gate]", StringComparison.Ordinal) && !after.Contains($"{loser} [gate]", StringComparison.Ordinal));
    }

    Console.WriteLine("\n-- 4. Quotas: two users behind one IP (limit 3 per window)");
    await using (TestHost h = await TestHost.StartAsync(root, tokens.Key, permits: 3, firstEdition: true))
    {
        for (int i = 0; i < 3; i++) await h.Ask(alice, "hello");
        Check("DEFECT REPRODUCED: first-edition key + default claim mapping: B is refused after A's 3 calls (429)",
            (await h.Ask(bob, "hello")).Status == 429);
    }
    await using (TestHost h = await TestHost.StartAsync(root, tokens.Key, permits: 3))
    {
        for (int i = 0; i < 3; i++) await h.Ask(alice, "hello");
        Check("the fix: A's fourth call is refused (429)", (await h.Ask(alice, "hello")).Status == 429);
        Check("B, same IP, still has a quota (200)", (await h.Ask(bob, "hello")).Status == 200);
        Check("fabrikam/alice, same \"sub\", still has a quota (200)", (await h.Ask(fabrikamAlice, "hello")).Status == 200);
    }

    Console.WriteLine("\n-- 5. Health probes never reach the model");
    await using (TestHost h = await TestHost.StartAsync(root, tokens.Key, permits: 100))
    {
        int before = h.Model.Calls;
        bool allLive = true;
        for (int i = 0; i < 100; i++) allLive &= await h.Status(null, HttpMethod.Get, "/health/live") == 200;
        bool ready = await h.Status(null, HttpMethod.Get, "/health/ready") == 200;
        Check($"100 liveness probes and one readiness probe: 200, {h.Model.Calls - before} model calls",
            allLive && ready && h.Model.Calls == before);
    }
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    try { Directory.Delete(root, recursive: true); } catch (IOException) { }
}

Console.WriteLine($"\nRESULT {passed}/{total}");
return passed == total ? 0 : 1;

namespace Ch17.IdentityTests
{
    /// <summary>Mints tokens the way an identity provider would, with one signing key per instance.</summary>
    internal sealed class Tokens
    {
        public const string Issuer = "identity-tests";
        public const string Audience = "contoso-expense-api";
        public byte[] Key { get; } = RandomNumberGenerator.GetBytes(32);

        public string Mint(string? tenant, string subject, string audience = Audience, DateTime? expires = null)
        {
            Dictionary<string, object> claims = new() { ["sub"] = subject };
            if (tenant is not null) claims["tid"] = tenant;
            DateTime exp = expires ?? DateTime.UtcNow.AddMinutes(10);
            return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = Issuer, Audience = audience, Claims = claims,
                NotBefore = exp.AddMinutes(-20), IssuedAt = exp.AddMinutes(-20), Expires = exp,
                SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Key), SecurityAlgorithms.HmacSha256),
            });
        }
    }

    /// <summary>One host on a TestServer. Validation parameters arrive through configuration, as user-jwts writes them.</summary>
    internal sealed class TestHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly HttpClient _client;

        private TestHost(WebApplication app, HistoryEchoChatClient model)
        {
            _app = app;
            _client = app.GetTestClient();
            Model = model;
        }

        public HistoryEchoChatClient Model { get; }

        public static async Task<TestHost> StartAsync(string root, byte[] signingKey, int permits, bool firstEdition = false, bool configureAudience = true)
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            Dictionary<string, string?> settings = new()
            {
                ["Authentication:Schemes:Bearer:ValidIssuer"] = Tokens.Issuer,
                ["Authentication:Schemes:Bearer:ValidAudiences:0"] = Tokens.Audience,
                ["Authentication:Schemes:Bearer:SigningKeys:0:Issuer"] = Tokens.Issuer,
                ["Authentication:Schemes:Bearer:SigningKeys:0:Value"] = Convert.ToBase64String(signingKey),
                ["Conversations:DatabasePath"] = Path.Combine(root, Guid.NewGuid().ToString("N") + ".db"),
                ["Quota:PermitLimit"] = permits.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Quota:WindowSeconds"] = "60",
            };
            if (!configureAudience) settings.Remove("Authentication:Schemes:Bearer:ValidAudiences:0");
            builder.Configuration.AddInMemoryCollection(settings);
            HistoryEchoChatClient model = new();
            AgentHostOptions options = new() { ChatClient = model, ServiceDefaults = false };
            if (firstEdition)
                options = options with { MapInboundClaims = true, QuotaPartition = FirstEditionPartition };
            AgentHost.Configure(builder, options);
            WebApplication app = builder.Build();
            app.Use((http, next) =>
            {
                http.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.1");   // one NAT for everybody
                return next(http);
            });
            AgentHost.Map(app);
            await app.StartAsync();
            return new TestHost(app, model);
        }

        // The first edition's partition key: "sub" (gone after the default mapping) or the IP.
        private static object FirstEditionPartition(HttpContext http) =>
            http.User.FindFirst("sub")?.Value ?? http.Connection.RemoteIpAddress?.ToString() ?? "anonymous";

        public async Task<int> Status(string? token, HttpMethod method, string path)
        {
            using HttpRequestMessage request = new(method, path);
            if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage response = await _client.SendAsync(request);
            return (int)response.StatusCode;
        }

        public async Task<string> NewConversation(string token)
        {
            using HttpRequestMessage request = new(HttpMethod.Post, "/conversations");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage response = await _client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return body.RootElement.GetProperty("conversationId").GetString()!;
        }

        public Task<(int Status, string Text)> Turn(string token, string id, string message) =>
            Post(token, $"/conversations/{id}/messages", message);

        public Task<(int Status, string Text)> Ask(string token, string message) => Post(token, "/ask", message);

        private async Task<(int, string)> Post(string token, string path, string message)
        {
            using HttpRequestMessage request = new(HttpMethod.Post, path) { Content = JsonContent.Create(new MessageRequest(message)) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage response = await _client.SendAsync(request);
            if (!response.IsSuccessStatusCode) return ((int)response.StatusCode, "");
            TurnReply? reply = await response.Content.ReadFromJsonAsync<TurnReply>();
            return ((int)response.StatusCode, reply?.Text ?? "");
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
