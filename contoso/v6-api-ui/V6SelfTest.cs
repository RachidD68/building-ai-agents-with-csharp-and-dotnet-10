using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ch17.AspNetCoreHost;
using Contoso.DurableResume;
using Microsoft.AspNetCore.TestHost;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Contoso.ApiUi;

/// <summary>
/// --self-test: v5's prepare runs as a child process, the v6 API runs on a
/// TestServer over the same store, then v5's resume runs as a child process
/// and the API reports the result. Tokens minted here, fake model, offline.
/// </summary>
internal static class V6SelfTest
{
    private static int _passed, _total;

    public static async Task<int> RunAsync()
    {
        Console.WriteLine("=== Contoso v6: the API over v5 (self-test, offline) ===\n");
        string store = Path.Combine(Path.GetTempPath(), $"contoso-v6-selftest-{Guid.NewGuid():N}");
        try
        {
            Console.WriteLine("-- 1. v5 prepare (child process): Alice's taxi claim waits for review");
            (int prepCode, string prepOut) = V5("prepare", "--store", store);
            Check("prepare exits 0 with the claim AwaitingReview", prepCode == 0 && prepOut.Contains("Intake: AwaitingReview", StringComparison.Ordinal));
            Guid op = new DurableClaims(Path.Combine(store, "app.db")).LoadSession("latest")!.OperationId;

            byte[] key = RandomNumberGenerator.GetBytes(32);
            await using (WebApplication app = await StartAsync(store, key))
            {
                HttpClient http = app.GetTestClient();
                string alice = Mint(key, "contoso", "E-1001"), bob = Mint(key, "contoso", "E-2002"), rita = Mint(key, "contoso", "rita");
                string fabrikamAlice = Mint(key, "fabrikam", "E-1001"), fabrikamRita = Mint(key, "fabrikam", "rita");

                Console.WriteLine("\n-- 2. Who can see the claim (GET /claims/{id})");
                Check("alice (the requester): 200 AwaitingReview", await StateAsync(http, alice, op) == "AwaitingReview");
                Check("rita (finance reviewer, same tenant): 200", await StateAsync(http, rita, op) == "AwaitingReview");
                Check("bob (same tenant, not his claim): 404", await StateAsync(http, bob, op) == "404");
                Check("fabrikam/E-1001 (same subject, other tenant): 404", await StateAsync(http, fabrikamAlice, op) == "404");
                Check("no token: 401", await StateAsync(http, null, op) == "401");

                Console.WriteLine("\n-- 3. Who can decide (POST /claims/{id}/decision); the reviewer is the token's subject");
                Check("alice approves her own claim: 403 separation_of_duties",
                    await DecideAsync(http, alice, op, """{"decision":"approve"}""") == "403 separation_of_duties");
                Check("bob sends {\"reviewer\":\"rita\"} in the body: 404 (he cannot see it; the field is never read)",
                    await DecideAsync(http, bob, op, """{"decision":"approve","reviewer":"rita"}""") == "404");
                Check("fabrikam/rita (same subject as the reviewer, other tenant): 404",
                    await DecideAsync(http, fabrikamRita, op, """{"decision":"approve"}""") == "404");
                Task<string> first = Task.Run(() => DecideAsync(http, rita, op, """{"decision":"approve"}"""));
                Task<string> second = Task.Run(() => DecideAsync(http, rita, op, """{"decision":"approve"}"""));
                string[] both = [await first, await second];
                Check($"rita approves twice at once (Task.Run): {both[0]} and {both[1]}",
                    both.Count(r => r == "200 rita") == 1 && both.Count(r => r == "409 already_decided") == 1);
            }

            Console.WriteLine("\n-- 4. v5 resume (child process) pays once; the API reports it");
            (int resCode, string resOut) = V5("resume", op.ToString("D"), "--store", store);
            Check("resume exits 0, [DELIVERED]", resCode == 0 && resOut.Contains("[DELIVERED]", StringComparison.Ordinal));
            await using (WebApplication app = await StartAsync(store, key))
            {
                HttpClient http = app.GetTestClient();
                Check("alice: Delivered", await StateAsync(http, Mint(key, "contoso", "E-1001"), op) == "Delivered");
                Check("a decision after payment: 409 not_awaiting_review",
                    await DecideAsync(http, Mint(key, "contoso", "rita"), op, """{"decision":"reject"}""") == "409 not_awaiting_review");
                Check($"one payment in the provider's journal: {DurableClaims.JournalReferences(Path.Combine(store, "provider.db")).Count}",
                    DurableClaims.JournalReferences(Path.Combine(store, "provider.db")).Count == 1);
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(store, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        Console.WriteLine($"\nRESULT {_passed}/{_total}");
        return _passed == _total ? 0 : 1;
    }

    private static async Task<WebApplication> StartAsync(string store, byte[] key)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Schemes:Bearer:ValidIssuer"] = "contoso-v6",
            ["Authentication:Schemes:Bearer:ValidAudiences:0"] = "contoso-expense-api",
            ["Authentication:Schemes:Bearer:SigningKeys:0:Issuer"] = "contoso-v6",
            ["Authentication:Schemes:Bearer:SigningKeys:0:Value"] = Convert.ToBase64String(key),
            ["Conversations:DatabasePath"] = Path.Combine(store, "conversations.db"),
        });
        AgentHost.Configure(builder, new AgentHostOptions { ChatClient = new HistoryEchoChatClient(), ServiceDefaults = false });
        WebApplication app = builder.Build();
        AgentHost.Map(app);
        ClaimsApi.Map(app, store);
        await app.StartAsync();
        return app;
    }

    private static async Task<string> StateAsync(HttpClient http, string? token, Guid op)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, $"/claims/{op:D}");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode) return ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return (await response.Content.ReadFromJsonAsync<ClaimView>())!.State;
    }

    private static async Task<string> DecideAsync(HttpClient http, string token, Guid op, string json)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, $"/claims/{op:D}/decision")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        int code = (int)response.StatusCode;
        if (body.Length == 0) return code.ToString(System.Globalization.CultureInfo.InvariantCulture);
        using JsonDocument doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("refused", out JsonElement r) ? $"{code} {r.GetString()}"
            : doc.RootElement.TryGetProperty("reviewer", out JsonElement who) ? $"{code} {who.GetString()}"
            : code.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string Mint(byte[] key, string tenant, string subject) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Issuer = "contoso-v6", Audience = "contoso-expense-api",
        Claims = new Dictionary<string, object> { ["sub"] = subject, ["tid"] = tenant },
        Expires = DateTime.UtcNow.AddMinutes(10),
        SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256),
    });

    // v5's own assembly, run with this app's runtimeconfig and deps (a superset of v5's).
    private static (int Code, string Output) V5(params string[] args)
    {
        string dir = AppContext.BaseDirectory;
        string self = Path.GetFileNameWithoutExtension(typeof(V6SelfTest).Assembly.Location);
        string host = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "") == "dotnet" ? Environment.ProcessPath! : "dotnet";
        ProcessStartInfo psi = new(host)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = dir,
        };
        foreach (string a in (string[])["exec", "--runtimeconfig", Path.Combine(dir, self + ".runtimeconfig.json"),
                     "--depsfile", Path.Combine(dir, self + ".deps.json"), Path.Combine(dir, "Contoso.DurableResume.dll"), .. args])
            psi.ArgumentList.Add(a);
        using Process p = Process.Start(psi)!;
        Task<string> stdout = p.StandardOutput.ReadToEndAsync(), stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(TimeSpan.FromSeconds(120)))
        {
            p.Kill(entireProcessTree: true);
            return (-1, "timed out");
        }
        return (p.ExitCode, stdout.Result + stderr.Result);
    }

    private static void Check(string name, bool ok)
    {
        _total++;
        if (ok) _passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    }
}
