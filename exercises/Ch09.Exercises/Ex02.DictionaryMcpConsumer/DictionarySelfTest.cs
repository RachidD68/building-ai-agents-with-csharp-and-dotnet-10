using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Ex02.DictionaryMcpServer;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Ex02.DictionaryMcpConsumer;

/// <summary>
/// Offline check: dotnet run -- --self-test. The server's own setup (linked files)
/// runs on a 127.0.0.1 port; tokens are signed with a key made for this run.
/// </summary>
internal static class DictionarySelfTest
{
    public const string Instructions =
        "You are a helpful dictionary assistant. Use the tools to look up definitions and synonyms. Provide helpful context.";

    const string Issuer = "https://issuer.invalid/ex02";
    static int s_passed, s_total;

    public static async Task<int> RunAsync()
    {
        Console.WriteLine("=== Dictionary MCP consumer: self-test ===");

        Console.WriteLine("\n-- Intermediate: the dictionary over streamable HTTP --");
        var (open, openUri) = await StartAsync(s => s.AddOpenDictionaryMcp(), a => a.MapDictionaryMcp(requireToken: false));
        Uri openEndpoint = new(openUri, "/mcp");
        await using (McpClient client = await ConnectAsync(openEndpoint, HttpTransportMode.StreamableHttp))
        {
            IList<McpClientTool> tools = await client.ListToolsAsync();
            Expect("StreamableHttp: the consumer lists the three tools", Names(tools) == "add_word, define_word, get_synonyms",
                $"tools: {Names(tools)}");

            ScriptedChain model = new();
            ChatClientAgent agent = new(model, instructions: Instructions, name: "DictionaryAssistant", tools: [.. tools]);
            AgentResponse answer = await agent.RunAsync("Define 'reticent' and give me synonyms.", await agent.CreateSessionAsync());
            Expect("the agent chains define_word then get_synonyms over HTTP",
                model.Called == "define_word,get_synonyms" && answer.Text.Contains("Reluctant") && answer.Text.Contains("taciturn"),
                $"calls: {model.Called}");
        }

        string sse = await OutcomeAsync(async () =>
        {
            await using McpClient pinned = await ConnectAsync(openEndpoint, HttpTransportMode.Sse);
            return $"connected, {(await pinned.ListToolsAsync()).Count} tools";
        });
        Expect("an SSE-pinned client cannot use a stateless streamable-HTTP server",
            !sse.StartsWith("connected, 3"), sse);
        await open.StopAsync();

        Console.WriteLine("\n-- Challenge: dictionary.read / dictionary.write --");
        SymmetricSecurityKey key = new(RandomNumberGenerator.GetBytes(32));
        Uri? resource = null;
        var (secured, securedUri) = await StartAsync(
            s => s.AddProtectedDictionaryMcp(resource!.ToString(), Issuer, key),
            a => a.MapDictionaryMcp(requireToken: true),
            baseUri => resource = new Uri(baseUri, "/mcp"));
        Uri endpoint = resource!;

        string none = await OutcomeAsync(async () =>
        {
            await using McpClient anon = await ConnectAsync(endpoint, HttpTransportMode.StreamableHttp);
            return "connected";
        });
        Expect("no token: refused with 401", none.Contains("401"), none);

        await using (McpClient reader = await ConnectAsync(endpoint, HttpTransportMode.StreamableHttp, Mint(key, endpoint, "dictionary.read")))
        {
            IList<McpClientTool> tools = await reader.ListToolsAsync();
            Expect("dictionary.read: discovers define_word and get_synonyms, not add_word",
                Names(tools) == "define_word, get_synonyms", $"tools: {Names(tools)}");
            string lookup = await CallAsync(reader, "define_word", new() { ["word"] = "candid" });
            Expect("dictionary.read: define_word runs", lookup.StartsWith("ok: candid:"), lookup);
            string write = await CallAsync(reader, "add_word", new() { ["word"] = "laconic", ["definition"] = "Using very few words." });
            Expect("dictionary.read: add_word called by name is refused", write.StartsWith("refused: "), write);
            string after = await CallAsync(reader, "define_word", new() { ["word"] = "laconic" });
            Expect("... and nothing was written", after.Contains("not found"), after);
        }

        await using (McpClient writer = await ConnectAsync(endpoint, HttpTransportMode.StreamableHttp,
            Mint(key, endpoint, "dictionary.read dictionary.write")))
        {
            string add = await CallAsync(writer, "add_word", new() { ["word"] = "laconic", ["definition"] = "Using very few words." });
            string again = await CallAsync(writer, "add_word", new() { ["word"] = "laconic", ["definition"] = "Something else." });
            string define = await CallAsync(writer, "define_word", new() { ["word"] = "laconic" });
            Expect("dictionary.write: add_word adds once; the second call changes nothing",
                add == "ok: Added 'laconic'." && again.Contains("nothing changed") && define.Contains("Using very few words."),
                $"{add} / {again} / {define}");
        }

        string wrongAudience = await OutcomeAsync(async () =>
        {
            await using McpClient c = await ConnectAsync(endpoint, HttpTransportMode.StreamableHttp,
                Mint(key, new Uri("https://another-dictionary.invalid/mcp"), "dictionary.read dictionary.write"));
            return "connected";
        });
        Expect("a token for another server: refused with 401", wrongAudience.Contains("401"), wrongAudience);
        await secured.StopAsync();

        Console.WriteLine($"\nRESULT {s_passed}/{s_total}");
        return s_passed == s_total ? 0 : 1;
    }

    static string Mint(SecurityKey key, Uri audience, string scopes) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = audience.ToString(),
            Expires = DateTime.UtcNow.AddMinutes(5),
            Claims = new Dictionary<string, object> { ["sub"] = "ex02-consumer", ["scope"] = scopes },
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
        });

    static async Task<(WebApplication, Uri)> StartAsync(
        Action<IServiceCollection> services, Action<WebApplication> pipeline, Action<Uri>? beforeBuild = null)
    {
        int port;
        using (TcpListener l = new(IPAddress.Loopback, 0)) { l.Start(); port = ((IPEndPoint)l.LocalEndpoint).Port; }
        Uri baseUri = new($"http://127.0.0.1:{port}");
        beforeBuild?.Invoke(baseUri);
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [], ContentRootPath = AppContext.BaseDirectory, EnvironmentName = "Production",
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(baseUri.ToString());
        services(builder.Services);
        WebApplication app = builder.Build();
        pipeline(app);
        await app.StartAsync();
        return (app, baseUri);
    }

    static Task<McpClient> ConnectAsync(Uri endpoint, HttpTransportMode mode, string? token = null)
    {
        HttpClientTransportOptions options = new() { Endpoint = endpoint, TransportMode = mode };
        if (token is not null) options.AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" };
        return McpClient.CreateAsync(new HttpClientTransport(options));
    }

    static async Task<string> CallAsync(McpClient client, string tool, Dictionary<string, object?> args)
    {
        try
        {
            CallToolResult r = await client.CallToolAsync(tool, args);
            return (r.IsError == true ? "tool error: " : "ok: ") + string.Join(" ", r.Content.OfType<TextContentBlock>().Select(t => t.Text));
        }
        catch (McpException ex) { return $"refused: {ex.Message}"; }
    }

    static async Task<string> OutcomeAsync(Func<Task<string>> action)
    {
        try { return await action(); }
        catch (Exception ex) when (ex is HttpRequestException or McpException or TimeoutException or InvalidOperationException)
        {
            string message = ex.Message.ReplaceLineEndings(" ");
            return $"{ex.GetType().Name}: {(message.Length > 110 ? message[..110] + "..." : message)}";
        }
    }

    static string Names(IEnumerable<McpClientTool> tools) => string.Join(", ", tools.Select(t => t.Name).Order());

    static void Expect(string name, bool ok, string? detail = null)
    {
        s_total++;
        if (ok) s_passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
        if (detail is not null) Console.WriteLine($"        {detail}");
    }
}

/// <summary>Scripted model: define_word, then get_synonyms, then an answer from both results.</summary>
internal sealed class ScriptedChain : IChatClient
{
    private readonly List<string> _results = [];
    private readonly List<string> _called = [];
    public string Called => string.Join(",", _called);

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (messages.Last().Contents.OfType<FunctionResultContent>().FirstOrDefault() is { } result)
            _results.Add(result.Result?.ToString() ?? "");
        string? next = _called.Count switch { 0 => "define_word", 1 => "get_synonyms", _ => null };
        if (next is null)
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Join(" ", _results))));
        _called.Add(next);
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent($"call-{_called.Count}", next, new Dictionary<string, object?> { ["word"] = "reticent" })])));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (ChatResponseUpdate u in (await GetResponseAsync(messages, options, cancellationToken)).ToChatResponseUpdates()) yield return u;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    public void Dispose() { }
}
