using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Ex03.ContainerizedDeploy;

/// <summary>dotnet run -- --self-test (also run inside the image by check-image.ps1).</summary>
internal static class DeploySelfTest
{
    private static int _passed, _total;

    public static async Task<int> RunAsync()
    {
        Console.WriteLine("=== Ex03.ContainerizedDeploy: self-test (TestServer, minted tokens, fake model, offline) ===\n");
        byte[] key = RandomNumberGenerator.GetBytes(32);
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Schemes:Bearer:ValidIssuer"] = "ex03",
            ["Authentication:Schemes:Bearer:ValidAudiences:0"] = "ex03-api",
            ["Authentication:Schemes:Bearer:SigningKeys:0:Issuer"] = "ex03",
            ["Authentication:Schemes:Bearer:SigningKeys:0:Value"] = Convert.ToBase64String(key),
            ["Quota:PermitLimit"] = "3",
        });
        await using WebApplication app = DeployApp.Build(builder, new OkChatClient());
        app.Use((http, next) =>
        {
            http.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.1");
            return next(http);
        });
        await app.StartAsync();
        HttpClient http = app.GetTestClient();
        string alice = Mint(key, "contoso", "alice", "ex03-api"), bob = Mint(key, "contoso", "bob", "ex03-api");

        Check("no token: 401", await Chat(http, null) == 401);
        Check("wrong audience: 401", await Chat(http, Mint(key, "contoso", "alice", "another-api")) == 401);
        int[] a = [await Chat(http, alice), await Chat(http, alice), await Chat(http, alice), await Chat(http, alice)];
        Check($"alice from 10.0.0.1, four calls with a limit of 3: {string.Join(" ", a)}", a.SequenceEqual([200, 200, 200, 429]));
        Check("bob from the same IP still has his own quota: 200", await Chat(http, bob) == 200);
        Check("liveness answers without a token: 200", (await http.GetAsync("/health/live")).IsSuccessStatusCode);

        await app.StopAsync();
        Console.WriteLine($"\nRESULT {_passed}/{_total}");
        return _passed == _total ? 0 : 1;
    }

    private static async Task<int> Chat(HttpClient http, string? token)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "/chat") { Content = JsonContent.Create(new ChatRequest("hello")) };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await http.SendAsync(request);
        return (int)response.StatusCode;
    }

    private static string Mint(byte[] key, string tenant, string subject, string audience) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Issuer = "ex03", Audience = audience,
        Claims = new Dictionary<string, object> { ["sub"] = subject, ["tid"] = tenant },
        Expires = DateTime.UtcNow.AddMinutes(5),
        SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256),
    });

    private static void Check(string name, bool ok)
    {
        _total++;
        if (ok) _passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    }

    private sealed class OkChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "OK")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "OK");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
