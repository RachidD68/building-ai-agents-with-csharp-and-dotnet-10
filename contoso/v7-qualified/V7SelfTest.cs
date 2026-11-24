using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Ch17.AspNetCoreHost;
using Contoso.ApiUi;
using Contoso.DurableResume;
using Microsoft.AspNetCore.TestHost;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Contoso.Qualified;

/// <summary>
/// --self-test: (1) the release profile on a TestServer: reads answer, a disabled
/// capability is refused with its reason, after authentication; (2) the profile's
/// refusals; (3) the handoff drill on the qualification profile; (4) the tools each
/// caller is offered and (5) the answer contract, on scripted models
/// (V7SelfTest.Assistant.cs). --drill runs (3) alone. v5's verbs run as child processes on the same store; tokens minted here;
/// fake model; offline. Simulated effects only.
/// </summary>
internal static partial class V7SelfTest
{
    private static int _passed, _total;

    public static async Task<int> RunAsync(bool drillOnly)
    {
        Console.WriteLine(drillOnly
            ? "=== Contoso v7: the handoff drill (offline, simulated effects) ===\n"
            : "=== Contoso v7: the qualified version (self-test, offline) ===\n");
        string store = Path.Combine(Path.GetTempPath(), $"contoso-v7-selftest-{Guid.NewGuid():N}");
        byte[] key = RandomNumberGenerator.GetBytes(32);
        try
        {
            if (!drillOnly)
            {
                await ReleaseGateAsync(store + "-release", key);
                Refusals(store + "-refusals");
            }
            await DrillAsync(store, key);
            if (!drillOnly)
            {
                await ToolsByRoleAsync();
                await AnswerContractAsync();
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (string d in new[] { store, store + "-release", store + "-refusals" })
                try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        Console.WriteLine($"\nRESULT {_passed}/{_total}");
        return _passed == _total ? 0 : 1;
    }

    // ------------------------------------------------------------------
    private static async Task ReleaseGateAsync(string store, byte[] key)
    {
        Console.WriteLine("-- 1. The release profile: what ships, what is refused and why");
        (int prep, _) = V5("prepare", "--store", store);
        Guid op = new DurableClaims(Path.Combine(store, "app.db")).LoadSession("latest")!.OperationId;
        await using WebApplication app = await StartAsync(store, key, "release");
        HttpClient http = app.GetTestClient();
        string alice = Mint(key, "contoso", "E-1001"), rita = Mint(key, "contoso", "rita");

        Check($"v5 prepare, then alice reads her claim: {await StateAsync(http, alice, op)}", prep == 0 && await StateAsync(http, alice, op) == "AwaitingReview");
        ClaimReview? review = await ReviewAsync(http, rita, op);
        Check($"rita's review screen still answers (claims.read): {review?.Amount} {review?.Currency}", review is not null);
        (int code, string body) = await PostAsync(http, rita, $"/claims/{op:D}/approval",
            new ApprovalRequest(review?.AccountVersion ?? 0, review?.Digest ?? "", "approve"));
        using (JsonDocument d = JsonDocument.Parse(body))
            Check($"rita approves: {code} {d.RootElement.GetProperty("refused").GetString()} ({d.RootElement.GetProperty("capability").GetString()})",
                code == 503 && d.RootElement.GetProperty("capability").GetString() == "claims.decide"
                && d.RootElement.GetProperty("reason").GetString()!.Length > 0);
        (int decide, _) = await PostAsync(http, rita, $"/claims/{op:D}/decision", new { decision = "approve" });
        (int anonymous, _) = await PostAsync(http, null, $"/claims/{op:D}/decision", new { decision = "approve" });
        Check($"POST /decision: {decide}; with no token: {anonymous} (authentication runs before the gate)", decide == 503 && anonymous == 401);
        Check($"the claim is still {await StateAsync(http, alice, op)}: nothing was recorded", await StateAsync(http, alice, op) == "AwaitingReview");
        using HttpResponseMessage rel = await http.SendAsync(Auth(HttpMethod.Get, "/release", alice));
        using JsonDocument info = JsonDocument.Parse(await rel.Content.ReadAsStringAsync());
        string[] disabled = [.. info.RootElement.GetProperty("capabilities").EnumerateArray()
            .Where(c => !c.GetProperty("enabled").GetBoolean()).Select(c => c.GetProperty("name").GetString()!)];
        Check($"GET /release: candidate {info.RootElement.GetProperty("candidate").GetString()}, profile {info.RootElement.GetProperty("profile").GetString()}, disabled: {string.Join(", ", disabled)}",
            info.RootElement.GetProperty("profile").GetString() == "release" && disabled is ["claims.decide"]);
    }

    // ------------------------------------------------------------------
    private static void Refusals(string dir)
    {
        Console.WriteLine("\n-- 2. Refusals at start");
        Directory.CreateDirectory(dir);
        CapabilityFile shipped = JsonSerializer.Deserialize<CapabilityFile>(File.ReadAllText(ReleaseProfile.DefaultFile), JsonSerializerOptions.Web)!;
        string Write(CapabilityFile f, string name)
        {
            string p = Path.Combine(dir, name);
            File.WriteAllText(p, JsonSerializer.Serialize(f, JsonSerializerOptions.Web));
            return p;
        }
        string noReason = Write(shipped with { Capabilities = [.. shipped.Capabilities.Select(c => c.Enabled ? c : c with { Reason = "" })] }, "no-reason.json");
        Check($"a capability disabled with no reason: {Refused(() => ReleaseProfile.Load(noReason, "release"))}",
            Refused(() => ReleaseProfile.Load(noReason, "release")).Contains("no reason", StringComparison.Ordinal));
        Check($"an unknown profile 'prod': {Refused(() => ReleaseProfile.Load(ReleaseProfile.DefaultFile, "prod"))}",
            Refused(() => ReleaseProfile.Load(ReleaseProfile.DefaultFile, "prod")).Contains("unknown profile", StringComparison.Ordinal));
        // A route added to the API without a capability: drop GET /agents from the file, so it is mapped but undeclared.
        string missing = Write(shipped with { Capabilities = [.. shipped.Capabilities.Select(c => c with { Routes = [.. c.Routes.Where(r => r != "GET /agents")] })] }, "missing.json");
        string why = "";
        WebApplication? a = null;
        try
        {
            a = Builder(dir, RandomNumberGenerator.GetBytes(32)).Build();
            AgentHost.Map(a);
            ReleaseProfile.Load(missing, "release").Map(InterfaceApi.Map(ClaimsApi.Map(a, dir), dir));
        }
        catch (InvalidOperationException ex) { why = ex.Message; }
        finally { (a as IDisposable)?.Dispose(); }
        Check($"a mapped route in no capability stops the start: {why}", why.Contains("routes in no capability: GET /agents", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------
    private static async Task DrillAsync(string store, byte[] key)
    {
        Console.WriteLine($"\n-- {(_total == 0 ? "" : "3. ")}The handoff drill (qualification profile: every capability on, simulated provider)");
        string alice = Mint(key, "contoso", "E-1001"), rita = Mint(key, "contoso", "rita"), fabrikamAlice = Mint(key, "fabrikam", "E-1001");
        (int prep, _) = V5("prepare", "--store", store);
        Guid op = new DurableClaims(Path.Combine(store, "app.db")).LoadSession("latest")!.OperationId;
        Check("v5 prepare: a claim AwaitingReview on a fresh store", prep == 0);
        string journal = Path.Combine(store, "provider.db");

        Console.WriteLine("   a) restore a conversation after the API process is lost");
        string conversation;
        await using (WebApplication first = await StartAsync(store, key, "qualification"))
        {
            HttpClient http = first.GetTestClient();
            (int created, string body) = await PostAsync(http, alice, "/conversations", new { });
            using JsonDocument d = JsonDocument.Parse(body);
            conversation = d.RootElement.GetProperty("conversationId").GetString()!;
            (int turn1, _) = await PostAsync(http, alice, $"/conversations/{conversation}/messages", new MessageRequest("my taxi claim is waiting"));
            Check($"alice opens a conversation and sends a turn: {created}, {turn1}", created == 201 && turn1 == 200);
        }
        await using (WebApplication second = await StartAsync(store, key, "qualification"))
        {
            HttpClient http = second.GetTestClient();
            (int turn2, string body) = await PostAsync(http, alice, $"/conversations/{conversation}/messages", new MessageRequest("is it approved yet?"));
            string text = JsonDocument.Parse(body).RootElement.GetProperty("text").GetString()!;
            Check($"a new API process on the same store, alice's next turn: {turn2} \"{text}\"",
                turn2 == 200 && text == "user turns seen: my taxi claim is waiting | is it approved yet?");

            Console.WriteLine("   b) a valid id from another tenant");
            (int other, _) = await PostAsync(http, fabrikamAlice, $"/conversations/{conversation}/messages", new MessageRequest("show me"));
            string claim = await StateAsync(http, fabrikamAlice, op);
            Check($"fabrikam/E-1001 (same subject, other tenant) on alice's conversation: {other}; on her claim: {claim}", other == 404 && claim == "404");

            Console.WriteLine("   c) an approval shown before the account changed");
            ClaimReview shown = (await ReviewAsync(http, rita, op))!;
            (int changed, _) = V5("account-changed", "E-1001", "--store", store);
            (int stale, string staleBody) = await PostAsync(http, rita, $"/claims/{op:D}/approval", new ApprovalRequest(shown.AccountVersion, shown.Digest, "approve"));
            ClaimReview now = (await ReviewAsync(http, rita, op))!;
            Check($"rita approves what she saw at account version {shown.AccountVersion} after another claim committed: {stale} {Result(staleBody)}; the screen now shows {now.AccountVersion}",
                changed == 0 && stale == 409 && Result(staleBody) == "stale_version" && now.AccountVersion == shown.AccountVersion + 1);
            Check($"business state: {await StateAsync(http, alice, op)}, no decision recorded", await StateAsync(http, alice, op) == "AwaitingReview");
            (int ok, string okBody) = await PostAsync(http, rita, $"/claims/{op:D}/approval", new ApprovalRequest(now.AccountVersion, now.Digest, "approve"));
            Check($"she approves the current screen: {ok} {Result(okBody)}", ok == 200 && Result(okBody) == "approved");

            Console.WriteLine("   d) a payment whose outcome the worker never recorded");
            using (Child victim = Child.Start("resume", "latest", "--store", store, "--pause-at", "after-accept", "--lease-seconds", "3"))
            {
                bool reached = await victim.WaitForLineAsync("PAUSED at after-accept", TimeSpan.FromSeconds(60));
                victim.Kill();
                int atDeath = DurableClaims.JournalReferences(journal).Count;
                Check($"the worker is killed after the provider accepted the payment: {atDeath} payment in the provider's journal, the claim shows {await StateAsync(http, alice, op)}",
                    reached && atDeath == 1);
            }
            Thread.Sleep(TimeSpan.FromSeconds(3.5));                     // the dead worker's lease runs out
            (int resumed, string resumeOut) = V5("resume", "latest", "--store", store);
            int payments = DurableClaims.JournalReferences(journal).Count;
            string final = await StateAsync(http, alice, op);
            Check($"a new worker reconciles by reference: {(resumeOut.Contains("by reference", StringComparison.Ordinal) ? "\"by reference\"" : "no reference lookup")}; the claim is {final}; payments {payments}",
                resumed == 0 && resumeOut.Contains("by reference", StringComparison.Ordinal) && final is "Reconciled" or "Delivered" && payments == 1);
        }
    }

    // ------------------------------------------------------------------
    private static WebApplicationBuilder Builder(string store, byte[] key)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Schemes:Bearer:ValidIssuer"] = "contoso-v7",
            ["Authentication:Schemes:Bearer:ValidAudiences:0"] = "contoso-expense-api",
            ["Authentication:Schemes:Bearer:SigningKeys:0:Issuer"] = "contoso-v7",
            ["Authentication:Schemes:Bearer:SigningKeys:0:Value"] = Convert.ToBase64String(key),
            ["Conversations:DatabasePath"] = Path.Combine(store, "conversations.db"),
        });
        AgentHost.Configure(builder, new AgentHostOptions { ChatClient = new HistoryEchoChatClient(), ServiceDefaults = false })
            .Services.AddSingleton<ClaimFeed>().AddHostedService(sp => sp.GetRequiredService<ClaimFeed>());
        return builder;
    }

    private static async Task<WebApplication> StartAsync(string store, byte[] key, string profile)
    {
        Directory.CreateDirectory(store);
        WebApplication app = Builder(store, key).Build();
        AgentHost.Map(app);
        ReleaseProfile.Load(ReleaseProfile.DefaultFile, profile).Map(InterfaceApi.Map(ClaimsApi.Map(app, store), store));
        await app.StartAsync();
        return app;
    }

    private static string Mint(byte[] key, string tenant, string subject) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Issuer = "contoso-v7", Audience = "contoso-expense-api",
        Claims = new Dictionary<string, object> { ["sub"] = subject, ["tid"] = tenant },
        Expires = DateTime.UtcNow.AddMinutes(10),
        SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256),
    });

    private static HttpRequestMessage Auth(HttpMethod method, string path, string? token)
    {
        HttpRequestMessage r = new(method, path);
        if (token is not null) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return r;
    }

    private static async Task<string> StateAsync(HttpClient http, string? token, Guid op)
    {
        using HttpResponseMessage response = await http.SendAsync(Auth(HttpMethod.Get, $"/claims/{op:D}", token));
        if (!response.IsSuccessStatusCode) return ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return (await response.Content.ReadFromJsonAsync<ClaimView>())!.State;
    }

    private static async Task<ClaimReview?> ReviewAsync(HttpClient http, string token, Guid op)
    {
        using HttpResponseMessage response = await http.SendAsync(Auth(HttpMethod.Get, $"/claims/{op:D}/review", token));
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<ClaimReview>() : null;
    }

    private static async Task<(int Code, string Body)> PostAsync<T>(HttpClient http, string? token, string path, T body)
    {
        using HttpRequestMessage r = Auth(HttpMethod.Post, path, token);
        r.Content = JsonContent.Create(body);
        using HttpResponseMessage response = await http.SendAsync(r);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static string Result(string body)
    {
        using JsonDocument d = JsonDocument.Parse(body);
        return d.RootElement.TryGetProperty("result", out JsonElement r) ? r.GetString()! : "";
    }

    private static string Refused(Action a)
    {
        try { a(); return "accepted"; }
        catch (InvalidDataException ex) { return ex.Message; }
    }

    private static (int Code, string Output) V5(params string[] args)
    {
        using Child c = Child.Start(args);
        return c.WaitForExit();
    }

    private static void Check(string name, bool ok)
    {
        _total++;
        if (ok) _passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    }

    /// <summary>
    /// v5's own assembly as a child process, with this app's runtimeconfig and deps (a
    /// superset of v5's), stdin kept open so a --pause-at point waits until it is killed.
    /// </summary>
    private sealed class Child : IDisposable
    {
        private readonly Process _p;
        private readonly ConcurrentQueue<string> _lines = new();
        private readonly SemaphoreSlim _arrived = new(0);

        private Child(Process p)
        {
            _p = p;
            _p.OutputDataReceived += (_, e) => Add(e.Data);
            _p.ErrorDataReceived += (_, e) => Add(e.Data);
            _p.BeginOutputReadLine();
            _p.BeginErrorReadLine();
        }

        public static Child Start(params string[] args)
        {
            string dir = AppContext.BaseDirectory;
            string self = Path.GetFileNameWithoutExtension(typeof(V7SelfTest).Assembly.Location);
            string host = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "") == "dotnet" ? Environment.ProcessPath! : "dotnet";
            ProcessStartInfo psi = new(host)
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, WorkingDirectory = dir,
            };
            foreach (string a in (string[])["exec", "--runtimeconfig", Path.Combine(dir, self + ".runtimeconfig.json"),
                         "--depsfile", Path.Combine(dir, self + ".deps.json"), Path.Combine(dir, "Contoso.DurableResume.dll"), .. args])
                psi.ArgumentList.Add(a);
            return new Child(Process.Start(psi)!);
        }

        public async Task<bool> WaitForLineAsync(string fragment, TimeSpan timeout)
        {
            using CancellationTokenSource cts = new(timeout);
            try
            {
                while (!_lines.Any(l => l.Contains(fragment, StringComparison.Ordinal))) await _arrived.WaitAsync(cts.Token);
                return true;
            }
            catch (OperationCanceledException) { return false; }
        }

        public void Kill()
        {
            _p.Kill(entireProcessTree: true);
            _p.WaitForExit();
        }

        public (int Code, string Output) WaitForExit()
        {
            if (!_p.WaitForExit(TimeSpan.FromSeconds(120)))
            {
                _p.Kill(entireProcessTree: true);
                return (-1, "timed out");
            }
            _p.WaitForExit();
            return (_p.ExitCode, string.Join('\n', _lines));
        }

        public void Dispose() => _p.Dispose();

        private void Add(string? line)
        {
            if (line is null) return;
            _lines.Enqueue(line);
            _arrived.Release();
        }
    }
}
