using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Ch09.McpAccessTests;

/// <summary>Real Kestrel servers on 127.0.0.1, and MCP clients that talk to them.</summary>
public static class Loopback
{
    /// <summary>Starts an app on a free loopback port; <paramref name="configure"/> gets the base URL first.</summary>
    public static async Task<(WebApplication App, Uri BaseUri)> StartAsync(
        Action<IServiceCollection, Uri> services, Action<WebApplication> pipeline)
    {
        Uri baseUri = new($"http://127.0.0.1:{FreePort()}");
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(baseUri.ToString());
        services(builder.Services, baseUri);
        WebApplication app = builder.Build();
        pipeline(app);
        await app.StartAsync();
        return (app, baseUri);
    }

    public static Task<McpClient> ConnectAsync(Uri endpoint, string? token = null)
    {
        HttpClientTransportOptions options = new()
        {
            Endpoint = endpoint,
            TransportMode = HttpTransportMode.StreamableHttp,
        };
        if (token is not null)
            options.AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" };
        return McpClient.CreateAsync(new HttpClientTransport(options));
    }

    /// <summary>
    /// A client whose bearer token can change after it connected, as a refreshed,
    /// downgraded or revoked credential would: set <see cref="BearerSwitch.Token"/>.
    /// </summary>
    public static async Task<McpClient> ConnectSwitchableAsync(Uri endpoint, BearerSwitch bearer) =>
        await ConnectThroughAsync(endpoint, bearer);

    /// <summary>A client whose HTTP requests all pass through <paramref name="handler"/>.</summary>
    public static async Task<McpClient> ConnectThroughAsync(Uri endpoint, DelegatingHandler handler) =>
        await McpClient.CreateAsync(new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = endpoint, TransportMode = HttpTransportMode.StreamableHttp },
            new HttpClient(handler), loggerFactory: null, ownsHttpClient: true));

    /// <summary>The stdio wiring without a child process: two in-memory pipes.</summary>
    public static async Task<(IHost Server, McpClient Client)> ConnectInMemoryAsync(Type toolType)
    {
        Pipe clientToServer = new(), serverToClient = new();
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.ClearProviders();
        builder.Services.AddMcpServer()
            .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream())
            .WithTools([toolType]);
        IHost server = builder.Build();
        await server.StartAsync();
        McpClient client = await McpClient.CreateAsync(
            new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()));
        return (server, client);
    }

    /// <summary>
    /// Calls a tool and reports what came back, without throwing:
    /// "ok: ..." for a result, "tool error: ..." for isError, "refused: ..." for a protocol or HTTP error.
    /// </summary>
    public static async Task<string> CallAsync(McpClient client, string tool, Dictionary<string, object?> args)
    {
        try
        {
            CallToolResult result = await client.CallToolAsync(tool, args);
            string text = string.Join(" ", result.Content.OfType<TextContentBlock>().Select(t => t.Text));
            return (result.IsError == true ? "tool error: " : "ok: ") + OneLine(text);
        }
        catch (Exception ex) when (ex is McpException or HttpRequestException)
        {
            return $"refused: {ex.GetType().Name}: {OneLine(ex.Message)}";
        }
    }

    public static string OneLine(string s)
    {
        string flat = s.ReplaceLineEndings(" | ");
        return flat.Length <= 110 ? flat : flat[..110] + "...";
    }

    private static int FreePort()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

/// <summary>
/// Records what the client puts on the wire: per request, the JSON-RPC method, the
/// keys of params._meta, the response's status and media type, and whether an
/// Mcp-Session-Id header went either way.
/// </summary>
public sealed class WireRecorder() : DelegatingHandler(new HttpClientHandler())
{
    public const string MetaPrefix = "io.modelcontextprotocol/";   // printed once, not per key

    public List<string> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        string method = "-", meta = "";
        if (request.Content is not null)
        {
            using JsonDocument doc = JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct));
            JsonElement root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("method", out JsonElement m)) method = m.GetString() ?? "-";
                if (root.TryGetProperty("params", out JsonElement p) && p.ValueKind == JsonValueKind.Object
                    && p.TryGetProperty("_meta", out JsonElement metaElement) && metaElement.ValueKind == JsonValueKind.Object)
                    meta = string.Join(", ", metaElement.EnumerateObject().Select(e =>
                        e.Name.Replace(MetaPrefix, "") + (e.Value.ValueKind == JsonValueKind.String ? $"={e.Value.GetString()}" : "")));
            }
        }
        HttpResponseMessage response = await base.SendAsync(request, ct);
        bool session = request.Headers.Contains("Mcp-Session-Id") || response.Headers.Contains("Mcp-Session-Id");
        Requests.Add($"{request.Method} {method}; _meta: {meta} -> {(int)response.StatusCode} " +
                     $"{response.Content.Headers.ContentType?.MediaType}; Mcp-Session-Id: {(session ? "present" : "none")}");
        return response;
    }
}

/// <summary>Adds "Authorization: Bearer" with whatever token is current, or none.</summary>
public sealed class BearerSwitch() : DelegatingHandler(new HttpClientHandler())
{
    public string? Token { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (Token is not null) request.Headers.Authorization = new("Bearer", Token);
        return base.SendAsync(request, ct);
    }
}
