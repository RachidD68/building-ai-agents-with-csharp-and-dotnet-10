using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Ex02.MultiAgentApi;

/// <summary>dotnet run -- --self-test: the API on a TestServer, tokens minted here, a fake model.</summary>
internal static class MultiAgentSelfTest
{
    private static int _passed, _total;

    public static async Task<int> RunAsync()
    {
        Console.WriteLine("=== Ex02.MultiAgentApi: self-test (TestServer, minted tokens, fake model, offline) ===\n");
        byte[] key = RandomNumberGenerator.GetBytes(32);
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Schemes:Bearer:ValidIssuer"] = "ex02",
            ["Authentication:Schemes:Bearer:ValidAudiences:0"] = "ex02-api",
            ["Authentication:Schemes:Bearer:SigningKeys:0:Issuer"] = "ex02",
            ["Authentication:Schemes:Bearer:SigningKeys:0:Value"] = Convert.ToBase64String(key),
        });
        await using WebApplication app = MultiAgentApp.Build(builder, new EchoChatClient());
        await app.StartAsync();
        HttpClient http = app.GetTestClient();
        string alice = Mint(key, "contoso", "alice"), bob = Mint(key, "contoso", "bob");

        using (JsonDocument list = JsonDocument.Parse(await http.GetStringAsync("/agents")))
        {
            string[] names = [.. list.RootElement.EnumerateArray().Select(a => a.GetProperty("name").GetString()!)];
            Check($"GET /agents lists four keyed agents: {string.Join(", ", names)}", names.Length == 4);
        }
        Check("POST /agents/classifier with a token: 200", (await Send(http, alice, "/agents/classifier", "great product")).Status == 200);
        Check("POST /agents/poet (unknown key): 404", (await Send(http, alice, "/agents/poet", "hi")).Status == 404);
        Check("POST /agents/classifier without a token: 401", (await Send(http, null, "/agents/classifier", "hi")).Status == 401);

        string id;
        using (HttpRequestMessage create = Request(alice, "/agents/summarizer/conversations", null))
        using (HttpResponseMessage created = await http.SendAsync(create))
        {
            using JsonDocument body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            id = body.RootElement.GetProperty("conversationId").GetString()!;
        }
        await Send(http, alice, $"/agents/summarizer/conversations/{id}", "first turn");
        (int status, string text) second = await Send(http, alice, $"/agents/summarizer/conversations/{id}", "second turn");
        Check($"A's second turn sees the first: \"{second.text}\"", second.status == 200 && second.text == "first turn | second turn");
        Check("B on A's conversation id: 404", (await Send(http, bob, $"/agents/summarizer/conversations/{id}", "B")).Status == 404);
        Check("A's id under another agent's route: 404", (await Send(http, alice, $"/agents/translator/conversations/{id}", "x")).Status == 404);

        await app.StopAsync();
        Console.WriteLine($"\nRESULT {_passed}/{_total}");
        return _passed == _total ? 0 : 1;
    }

    private static HttpRequestMessage Request(string? token, string path, string? text)
    {
        HttpRequestMessage request = new(HttpMethod.Post, path);
        if (text is not null) request.Content = JsonContent.Create(new TextRequest(text));
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task<(int Status, string Text)> Send(HttpClient http, string? token, string path, string text)
    {
        using HttpRequestMessage request = Request(token, path, text);
        using HttpResponseMessage response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode) return ((int)response.StatusCode, "");
        AgentReply? reply = await response.Content.ReadFromJsonAsync<AgentReply>();
        return ((int)response.StatusCode, reply?.Text ?? "");
    }

    private static string Mint(byte[] key, string tenant, string subject) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Issuer = "ex02", Audience = "ex02-api",
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

    /// <summary>Answers with every user line it was sent, joined by " | ".</summary>
    private sealed class EchoChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                string.Join(" | ", messages.Where(m => m.Role == ChatRole.User).Select(m => m.Text)))));

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
