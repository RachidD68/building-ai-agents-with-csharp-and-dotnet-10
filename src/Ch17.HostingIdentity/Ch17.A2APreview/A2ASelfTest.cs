using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using A2A;
using Microsoft.Agents.AI.Hosting.A2A;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Ch17.A2APreview;

/// <summary>dotnet run -- --self-test: the A2A endpoint on a TestServer, offline.</summary>
internal static class A2ASelfTest
{
    private static int _passed, _total;

    public static async Task<int> RunAsync()
    {
        Console.WriteLine("=== Ch17.A2APreview: self-test (TestServer, fake model, offline) ===\n");
        byte[] key = RandomNumberGenerator.GetBytes(32);
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Schemes:Bearer:ValidIssuer"] = "a2a-self-test",
            ["Authentication:Schemes:Bearer:ValidAudiences:0"] = "contoso-expense-api",
            ["Authentication:Schemes:Bearer:SigningKeys:0:Issuer"] = "a2a-self-test",
            ["Authentication:Schemes:Bearer:SigningKeys:0:Value"] = Convert.ToBase64String(key),
            ["PublicBaseUrl"] = "http://localhost",
        });
        await using WebApplication app = A2AHost.Build(builder, new FixedReplyChatClient("Meals are capped at 40 EUR a day."));
        await app.StartAsync();
        HttpClient http = app.GetTestClient();

        Console.WriteLine("-- Routes the A2A hosting maps");
        IEnumerable<string> routes = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>().Select(e => $"{string.Join(",", e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])} /{e.RoutePattern.RawText?.TrimStart('/')}")
            .Distinct().Order();
        foreach (string route in routes) Console.WriteLine($"  {route}");

        Console.WriteLine("\n-- The agent card");
        AgentCard card = await new A2ACardResolver(http.BaseAddress!, http).GetAgentCardAsync();
        Check($"anonymous GET /.well-known/agent-card.json returns \"{card.Name}\"", card.Name == A2AHost.AgentName);
        Check("two interfaces, JSON-RPC and HTTP+JSON, both at " + A2AHost.Path,
            (card.SupportedInterfaces ?? []).Select(i => i.ProtocolBinding).Order().SequenceEqual(
                new[] { ProtocolBindingNames.HttpJson, ProtocolBindingNames.JsonRpc }.Order())
            && (card.SupportedInterfaces ?? []).All(i => i.Url?.EndsWith(A2AHost.Path, StringComparison.Ordinal) == true));
        Check("the card declares a bearer scheme and requires it",
            card.SecuritySchemes?["bearer"].HttpAuthSecurityScheme?.Scheme == "Bearer" && card.SecurityRequirements?.Count == 1);
        Check("no task-mode option: A2AServerRegistrationOptions has no AgentRunMode property",
            typeof(A2AServerRegistrationOptions).GetProperty("AgentRunMode") is null);

        Console.WriteLine("\n-- The bindings, without and with a token");
        Uri endpoint = new(http.BaseAddress!, A2AHost.Path);
        Check("JSON-RPC without a token: 401", await StatusAsync(http, null, endpoint) == 401);
        string token = Mint(key);
        HttpClient authed = app.GetTestClient();
        authed.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Check("JSON-RPC with a token: a message comes back, not a task",
            await ReplyAsync(new A2AClient(endpoint, authed)) == "Meals are capped at 40 EUR a day.");
        Check("HTTP+JSON with a token: the same message",
            await ReplyAsync(new A2AHttpJsonClient(endpoint, authed)) == "Meals are capped at 40 EUR a day.");
        Check("HTTP+JSON without a token fails (401)", await FailsUnauthorizedAsync(new A2AHttpJsonClient(endpoint, http)));
        using (HttpResponseMessage tasks = await http.GetAsync(A2AHost.Path + "/tasks"))
            Check("the task routes the SDK still maps are behind the token too: GET /tasks 401", (int)tasks.StatusCode == 401);

        await app.StopAsync();
        Console.WriteLine($"\nRESULT {_passed}/{_total}");
        return _passed == _total ? 0 : 1;
    }

    private static async Task<string?> ReplyAsync(IA2AClient client)
    {
        SendMessageResponse response = await client.SendMessageAsync("What is the meal limit?");
        return response.PayloadCase == SendMessageResponseCase.Message
            ? string.Concat((response.Message?.Parts ?? []).Select(p => p.Text))
            : $"<{response.PayloadCase}>";
    }

    private static async Task<bool> FailsUnauthorizedAsync(IA2AClient client)
    {
        try
        {
            await client.SendMessageAsync("What is the meal limit?");
            return false;
        }
        catch (Exception e)
        {
            Console.WriteLine($"  ({e.GetType().Name}: {e.Message.Split('\n')[0]})");
            return e.Message.Contains("401", StringComparison.Ordinal) || e.Message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static async Task<int> StatusAsync(HttpClient http, string? token, Uri endpoint)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, endpoint)
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":"1","method":"SendMessage","params":{}}""", Encoding.UTF8, "application/json"),
        };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await http.SendAsync(request);
        return (int)response.StatusCode;
    }

    private static string Mint(byte[] key) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Issuer = "a2a-self-test", Audience = "contoso-expense-api",
        Claims = new Dictionary<string, object> { ["sub"] = "orchestrator", ["tid"] = "contoso" },
        Expires = DateTime.UtcNow.AddMinutes(5),
        SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256),
    });

    private static void Check(string name, bool ok)
    {
        _total++;
        if (ok) _passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    }

    private sealed class FixedReplyChatClient(string reply) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, reply);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
