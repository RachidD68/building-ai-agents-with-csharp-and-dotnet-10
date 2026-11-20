using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
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
        await McpClient.CreateAsync(new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = endpoint, TransportMode = HttpTransportMode.StreamableHttp },
            new HttpClient(bearer), loggerFactory: null, ownsHttpClient: true));

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
