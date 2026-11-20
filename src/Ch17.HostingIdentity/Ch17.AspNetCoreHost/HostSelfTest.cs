using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Security.Claims;
using System.Text;
using AgenticAI.Common.Configuration;
using AgenticAI.Common.Extensions;
using AgenticAI.Common.Providers;
using Microsoft.Extensions.AI;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Ch17.AspNetCoreHost;

/// <summary>
/// dotnet run -- --self-test. Offline: tokens are minted and validated in
/// process, and the model is an HTTP handler that answers 503. The end-to-end
/// two-user proof over HTTP is Ch17.IdentityTests.
/// </summary>
internal static class HostSelfTest
{
    private static int _passed, _total;

    public static async Task<int> RunAsync()
    {
        Console.WriteLine("=== Ch17.AspNetCoreHost: self-test (offline) ===\n");

        Console.WriteLine("-- Identity key from a validated token");
        ClaimsPrincipal alice = Validate(Mint(("tid", "contoso"), ("sub", "alice")), mapInbound: false);
        ClaimsPrincipal bob = Validate(Mint(("tid", "contoso"), ("sub", "bob")), mapInbound: false);
        ClaimsPrincipal fabrikamAlice = Validate(Mint(("tid", "fabrikam"), ("sub", "alice")), mapInbound: false);
        Check("MapInboundClaims = false keeps \"sub\" and \"tid\": key contoso/alice",
            CallerIdentity.From(alice) is { TenantId: "contoso", SubjectId: "alice" });
        Check("a token without \"tid\" has no identity key (the route answers 403)",
            CallerIdentity.From(Validate(Mint(("sub", "alice")), mapInbound: false)) is null);

        Console.WriteLine("\n-- Rate-limit partition (two users behind one IP)");
        ClaimsPrincipal mappedAlice = Validate(Mint(("tid", "contoso"), ("sub", "alice")), mapInbound: true);
        ClaimsPrincipal mappedBob = Validate(Mint(("tid", "contoso"), ("sub", "bob")), mapInbound: true);
        Console.WriteLine($"  claim types after the default inbound mapping: " +
                          string.Join(", ", mappedAlice.Claims.Select(c => c.Type).Where(t => t.Contains('/')).Distinct()));
        object v1A = V1Partition(At(mappedAlice)), v1B = V1Partition(At(mappedBob));
        Check($"DEFECT REPRODUCED: the first edition's key (\"sub\" or the IP) gives both users {v1A}",
            v1A.Equals(v1B) && v1A.Equals("10.0.0.1"));
        Check("with the default mapping the host finds no identity key: it refuses (403), it never guesses",
            CallerIdentity.From(mappedAlice) is null);
        Check("the fix: alice and bob behind 10.0.0.1 get two partitions",
            !CallerIdentity.Partition(At(alice)).Equals(CallerIdentity.Partition(At(bob))));
        Check("the same subject in two tenants gets two partitions (never \"sub\" alone)",
            !CallerIdentity.Partition(At(alice)).Equals(CallerIdentity.Partition(At(fabrikamAlice))));
        Check("an anonymous caller falls back to its address",
            CallerIdentity.Partition(At(new ClaimsPrincipal(new ClaimsIdentity()))) is QuotaKey { Tenant: null, Subject: "10.0.0.1" });

        Console.WriteLine("\n-- One retry owner (the provider answers 503 every time)");
        int both = await CountSendsAsync(sdkRetries: true);
        int one = await CountSendsAsync(sdkRetries: false);
        Check($"DEFECT REPRODUCED: SDK retries on top of the resilience handler: {both} sends for one call", both == 16);
        Check($"the fix (AgenticAI.Common's OpenAI arm, maxRetries: 0): {one} sends for one call", one == 4);

        Console.WriteLine($"\nRESULT {_passed}/{_total}");
        return _passed == _total ? 0 : 1;
    }

    // The first edition's partition key, kept to reproduce the defect.
    internal static object V1Partition(HttpContext http) =>
        http.User.FindFirst("sub")?.Value ?? http.Connection.RemoteIpAddress?.ToString() ?? "anonymous";

    private static readonly SymmetricSecurityKey Key = new(Encoding.UTF8.GetBytes("ch17-self-test-signing-key-32-bytes!"));

    private static string Mint(params (string Type, string Value)[] claims) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "self-test", Audience = "contoso-expense-api",
            Claims = claims.ToDictionary(c => c.Type, c => (object)c.Value),
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(Key, SecurityAlgorithms.HmacSha256),
        });

    // The same handler JwtBearer uses, with its MapInboundClaims switch.
    private static ClaimsPrincipal Validate(string token, bool mapInbound)
    {
        TokenValidationResult result = new JsonWebTokenHandler { MapInboundClaims = mapInbound }
            .ValidateTokenAsync(token, new TokenValidationParameters
            {
                ValidIssuer = "self-test", ValidAudience = "contoso-expense-api", IssuerSigningKey = Key,
            }).GetAwaiter().GetResult();
        if (!result.IsValid) throw new InvalidOperationException("self-test token did not validate", result.Exception);
        return new ClaimsPrincipal(result.ClaimsIdentity);
    }

    private static HttpContext At(ClaimsPrincipal user)
    {
        DefaultHttpContext http = new() { User = user };
        http.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.1");
        return http;
    }

    private static async Task<int> CountSendsAsync(bool sdkRetries)
    {
        CountingHandler handler = new();
        ServiceCollection services = new();
        services.AddHttpClient("llm").ConfigurePrimaryHttpMessageHandler(() => handler).AddLlmResilienceHandler();
        await using ServiceProvider sp = services.BuildServiceProvider();
        HttpClient http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("llm");
        LlmSettings settings = new() { OpenAI = new OpenAISettings { ApiKey = "offline", Model = "test-model" } };
        IChatClient client = sdkRetries
            ? new OpenAI.OpenAIClient(new ApiKeyCredential("offline"),
                    new OpenAI.OpenAIClientOptions { Transport = new HttpClientPipelineTransport(http) })
                .GetChatClient("test-model").AsIChatClient()          // the SDK's default retry policy
            : new OpenAIChatClientProvider().CreateChatClient(http, settings);
        try { await client.GetResponseAsync("hello"); } catch (Exception) { /* every send fails */ }
        return handler.Sends;
    }

    private static void Check(string name, bool ok)
    {
        _total++;
        if (ok) _passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        private int _sends;
        public int Sends => _sends;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _sends);
            HttpResponseMessage response = new(HttpStatusCode.ServiceUnavailable);
            response.Headers.TryAddWithoutValidation("Retry-After", "0");   // no waiting between attempts
            return Task.FromResult(response);
        }
    }
}
