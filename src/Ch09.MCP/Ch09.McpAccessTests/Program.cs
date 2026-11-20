// ============================================================================
// MCP access matrix (offline)
//
//   dotnet run -- --self-test
//
// Starts real servers on 127.0.0.1 (Kestrel, a free port) and calls them with
// the MCP C# client, the way a second team would:
//   1. same tools over the stdio wiring (in-memory pipes) and over HTTP;
//   2. the unprotected HTTP server (Ch09.McpHttpServer's setup): any caller
//      lists and calls every tool -> DEFECT REPRODUCED;
//   3. the protected server (Ch09.ProtectedMcpServer's setup): metadata,
//      no token / wrong audience / expired / foreign key, scope per tool, and
//      a tool discovered with one token but invoked with another;
//   4. the client's destination guard (Ch09.McpConsumer).
// Tokens are signed with a key generated for this run; no identity provider,
// no network beyond loopback, no model. Tokens are never printed.
// ============================================================================

using System.Net;
using System.Text;
using System.Text.Json;
using Ch09.McpAccessTests;
using Ch09.McpConsumer;
using Ch09.McpHttpServer;
using Ch09.McpServer;
using Ch09.ProtectedMcpServer;
using ModelContextProtocol.Client;

if (!args.Contains("--self-test"))
{
    Console.WriteLine("Usage: dotnet run -- --self-test   (offline access matrix; RESULT n/n)");
    return 2;
}

int passed = 0, total = 0;
void Expect(string name, bool ok, string? detail = null)
{
    total++;
    if (ok) passed++;
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    if (detail is not null) Console.WriteLine($"        {detail}");
}

Dictionary<string, object?> lookup = new() { ["productId"] = 1003 };
Dictionary<string, object?> inventory = new() { ["sku"] = "KB-ERG-001" };
static string Names(IEnumerable<McpClientTool> tools) => string.Join(", ", tools.Select(t => t.Name).Order());

Console.WriteLine("=== MCP access matrix: self-test ===");

// ---------------------------------------------------------------------------
Console.WriteLine("\n-- 1. One tool set, two transports --");
var (pipeServer, pipeClient) = await Loopback.ConnectInMemoryAsync(typeof(ProductCatalogTools));
string viaStdio = Names(await pipeClient.ListToolsAsync());
await pipeClient.DisposeAsync();
await pipeServer.StopAsync();

var (openApp, openBase) = await Loopback.StartAsync(
    (services, _) => services.AddCatalogMcpOverHttp(),
    app => app.MapCatalogMcp());
Uri openEndpoint = new(openBase, "/mcp");
await using (McpClient anon = await Loopback.ConnectAsync(openEndpoint))
{
    string viaHttp = Names(await anon.ListToolsAsync());
    Expect("stdio wiring and streamable HTTP list the same tools", viaStdio == viaHttp && viaHttp.Length > 0,
        $"stdio: {viaStdio}; http: {viaHttp}");

    // -----------------------------------------------------------------------
    Console.WriteLine("\n-- 2. Unprotected HTTP server: who may call? --");
    Expect("DEFECT REPRODUCED: a caller with no token discovers every tool",
        viaHttp == "InventoryCheck, ProductLookup", $"listed: {viaHttp}");
    string anonCall = await Loopback.CallAsync(anon, "InventoryCheck", inventory);
    Expect("DEFECT REPRODUCED: ... and invokes them (stock levels to anyone who reaches the port)",
        anonCall.StartsWith("ok: ") && anonCall.Contains("Units Available"), anonCall);
}
await openApp.StopAsync();

// ---------------------------------------------------------------------------
Console.WriteLine("\n-- 3. Protected server: token for this resource, scope per tool --");
LocalIssuer issuer = new();
var (app, baseUri) = await Loopback.StartAsync(
    (services, b) => services.AddProtectedCatalogMcp(new ProtectedMcpSettings(new Uri(b, "/mcp").ToString(), LocalIssuer.Name, issuer.Key)),
    a => a.MapProtectedCatalogMcp());
Uri endpoint = new(baseUri, "/mcp");
string resource = endpoint.ToString();
using HttpClient http = new();

using (HttpResponseMessage meta = await http.GetAsync(new Uri(baseUri, "/.well-known/oauth-protected-resource/mcp")))
{
    using JsonDocument doc = JsonDocument.Parse(await meta.Content.ReadAsStringAsync());
    JsonElement root = doc.RootElement;
    string? metaResource = root.GetProperty("resource").GetString();
    string servers = string.Join(",", root.GetProperty("authorization_servers").EnumerateArray().Select(e => e.GetString()));
    string scopes = string.Join(" ", root.GetProperty("scopes_supported").EnumerateArray().Select(e => e.GetString()));
    Expect("protected resource metadata is public and names this endpoint, its issuer and scopes",
        meta.StatusCode == HttpStatusCode.OK && metaResource == resource && servers == LocalIssuer.Name
            && scopes == "catalog.read inventory.read",
        $"{(int)meta.StatusCode}; resource=<this endpoint>: {metaResource == resource}; authorization_servers={servers}; scopes_supported={scopes}");
}

async Task<(HttpStatusCode Status, string Challenge)> RawListAsync(string? token)
{
    var (status, challenge, _) = await RawPostAsync(token, """{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""");
    return (status, challenge);
}

async Task<(HttpStatusCode Status, string Challenge, string Body)> RawPostAsync(string? token, string json)
{
    using HttpRequestMessage request = new(HttpMethod.Post, endpoint)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };
    request.Headers.Accept.ParseAdd("application/json");
    request.Headers.Accept.ParseAdd("text/event-stream");
    if (token is not null) request.Headers.Authorization = new("Bearer", token);
    using HttpResponseMessage response = await http.SendAsync(request);
    return (response.StatusCode, string.Join(" ", response.Headers.WwwAuthenticate.Select(h => h.ToString())),
        await response.Content.ReadAsStringAsync());
}

var (noTokenStatus, challenge) = await RawListAsync(null);
string expectedMetadataUrl = new Uri(baseUri, "/.well-known/oauth-protected-resource/mcp").ToString();
Expect("no token: 401, and WWW-Authenticate points to the resource metadata",
    noTokenStatus == HttpStatusCode.Unauthorized && challenge.StartsWith("Bearer")
        && challenge.Contains($"resource_metadata=\"{expectedMetadataUrl}\""),
    $"{(int)noTokenStatus}; WWW-Authenticate: {challenge.Replace(baseUri.ToString().TrimEnd('/'), "http://127.0.0.1:<port>")}");

string allScopes = string.Join(" ", CatalogScopes.All);
(string Case, string Token)[] refusedTokens =
[
    ("wrong audience (a token minted for another server)", issuer.Mint("https://other-team.invalid/mcp", allScopes)),
    ("expired (10 min ago, past the 30 s clock skew)", issuer.Mint(resource, allScopes, TimeSpan.FromMinutes(-10))),
    ("signed with a key this server does not trust", issuer.Mint(resource, allScopes, signWith: new LocalIssuer().Key)),
    ("wrong issuer", issuer.Mint(resource, allScopes, issuer: "https://someone-else.invalid")),
];
foreach (var (name, token) in refusedTokens)
{
    var (status, _) = await RawListAsync(token);
    Expect($"{name}: 401 before any MCP handler runs", status == HttpStatusCode.Unauthorized, $"{(int)status}");
}

var (validStatus, _) = await RawListAsync(issuer.Mint(resource, allScopes));
Expect("the same request with a valid token is not refused", validStatus == HttpStatusCode.OK, $"{(int)validStatus}");

string readOnly = issuer.Mint(resource, CatalogScopes.Read);
string full = issuer.Mint(resource, allScopes);
string noScope = issuer.Mint(resource, null);

await using (McpClient none = await Loopback.ConnectAsync(endpoint, noScope))
{
    IList<McpClientTool> listed = await none.ListToolsAsync();
    string call = await Loopback.CallAsync(none, "ProductLookup", lookup);
    Expect("valid token, no scope: authenticated, but lists no tool and may call none",
        listed.Count == 0 && !call.StartsWith("ok: "), $"listed: [{Names(listed)}]; ProductLookup -> {call}");
}

await using (McpClient reader = await Loopback.ConnectAsync(endpoint, readOnly))
{
    IList<McpClientTool> listed = await reader.ListToolsAsync();
    Expect("catalog.read: discovery shows only ProductLookup", Names(listed) == "ProductLookup", $"listed: {Names(listed)}");
    string ok = await Loopback.CallAsync(reader, "ProductLookup", lookup);
    Expect("catalog.read: ProductLookup runs", ok.StartsWith("ok: ") && ok.Contains("MON-4K-003"), ok);
    string denied = await Loopback.CallAsync(reader, "InventoryCheck", inventory);
    Expect("catalog.read: InventoryCheck called by name anyway is refused", !denied.StartsWith("ok: ") && !denied.Contains("Units Available"), denied);
    // On the wire: the token is valid, so HTTP says 200; the refusal is a JSON-RPC error.
    var (callStatus, _, body) = await RawPostAsync(readOnly,
        """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"InventoryCheck","arguments":{"sku":"KB-ERG-001"}}}""");
    string error = body[(body.IndexOf('{') is var i and >= 0 ? i : 0)..].Trim();
    Expect("... on the wire: HTTP 200 carrying a JSON-RPC error, not a 403 and not a result",
        callStatus == HttpStatusCode.OK && error.Contains("\"error\"") && !error.Contains("Units Available"),
        $"{(int)callStatus}; {Loopback.OneLine(error)}");
}

Console.WriteLine("\n-- 3b. Discovery does not authorize invocation --");
BearerSwitch bearer = new() { Token = full };
await using (McpClient client = await Loopback.ConnectSwitchableAsync(endpoint, bearer))
{
    IList<McpClientTool> discovered = await client.ListToolsAsync();
    McpClientTool check = discovered.Single(t => t.Name == "InventoryCheck");
    Expect("full-scope token discovers both tools", Names(discovered) == "InventoryCheck, ProductLookup", $"listed: {Names(discovered)}");
    string fullCall = await Loopback.CallAsync(client, check.Name, inventory);
    Expect("... and the discovered InventoryCheck runs with it", fullCall.StartsWith("ok: ") && fullCall.Contains("Units Available"), fullCall);

    // Same client, same discovered tool; only the credential changes.
    bearer.Token = readOnly;
    string withReader = await Loopback.CallAsync(client, check.Name, inventory);
    Expect("token downgraded to catalog.read: the discovered InventoryCheck is refused", !withReader.StartsWith("ok: "), withReader);
    bearer.Token = null;
    string withNothing = await Loopback.CallAsync(client, check.Name, inventory);
    Expect("token gone: the discovered InventoryCheck is refused with 401",
        withNothing.StartsWith("refused: ") && withNothing.Contains("401"), withNothing);
    Expect("the tool list the client holds did not change", Names(discovered) == "InventoryCheck, ProductLookup");
}

// ---------------------------------------------------------------------------
Console.WriteLine("\n-- 4. Client destination guard --");
McpDestinationGuard guard = new([endpoint]);
HttpClientTransportOptions allowed = guard.CreateTransportOptions(endpoint, full);
Expect("allow-listed endpoint: transport options carry the bearer token",
    allowed.AdditionalHeaders?["Authorization"].StartsWith("Bearer ") == true);
string elsewhere = Refusal(() => guard.CreateTransportOptions(new Uri("https://attacker.invalid/mcp"), full));
Expect("endpoint not on the allow-list: refused before any request is sent", elsewhere.Contains("not on the allow-list"), elsewhere);
string checkedResource = await guard.CheckResourceMetadataAsync(http, endpoint);
Expect("resource metadata names the endpoint we meant: accepted", checkedResource == resource);

var (decoyApp, decoyBase) = await Loopback.StartAsync(
    (services, _) => services.AddProtectedCatalogMcp(new ProtectedMcpSettings("https://catalog.contoso.invalid/mcp", LocalIssuer.Name, issuer.Key)),
    a => a.MapProtectedCatalogMcp());
Uri decoyEndpoint = new(decoyBase, "/mcp");
string decoy = await RefusalAsync(() => new McpDestinationGuard([decoyEndpoint]).CheckResourceMetadataAsync(http, decoyEndpoint));
Expect("server whose metadata names another resource: refused, no token requested",
    decoy.Contains("refusing to request a token"), decoy.Replace(decoyBase.ToString().TrimEnd('/'), "http://127.0.0.1:<port>"));
await decoyApp.StopAsync();
await app.StopAsync();

Console.WriteLine($"\nRESULT {passed}/{total}");
return passed == total ? 0 : 1;

static string Refusal(Action action)
{
    try { action(); return "not refused"; }
    catch (InvalidOperationException ex) { return ex.Message; }
}

static async Task<string> RefusalAsync(Func<Task> action)
{
    try { await action(); return "not refused"; }
    catch (InvalidOperationException ex) { return ex.Message; }
}
