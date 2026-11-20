using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using AgenticAI.Testing;
using Ch08.ActionPolicy;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Ch20.PolicyEnforcement;

public sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>Contoso and Fabrikam, three accounts, one service and its stores.</summary>
public sealed class World
{
    public static readonly DateTimeOffset Now = new(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);
    public static readonly Caller Alice = new("contoso", "alice", new HashSet<string> { Permissions.SubmitReimbursement });
    public static readonly Caller Bob = new("contoso", "bob", new HashSet<string>());

    public AccountStore Accounts { get; } = new();
    public RevocationList Revocations { get; } = new();
    public RuleModes Modes { get; } = new();
    public AuditLog Audit { get; } = new();
    public ReimbursementService Service { get; }

    public World()
    {
        Accounts.Add(new EmployeeAccount("contoso", "E-1001", "EUR", 500m, 1));
        Accounts.Add(new EmployeeAccount("contoso", "E-1002", "EUR", 100m, 1));
        Accounts.Add(new EmployeeAccount("fabrikam", "E-2002", "EUR", 500m, 1));
        Service = new ReimbursementService(Accounts, Revocations, Modes, Audit, new FixedClock(Now));
    }
}

/// <summary>
/// The four ways in: a real HTTP API on 127.0.0.1 (Kestrel), a real MCP server
/// over in-memory pipes (the stdio wiring), and two agent runs shaped like the
/// A2A host (RunAsync) and the AG-UI host (RunStreamingAsync), each with a
/// scripted model that asks for the tool. Every call also sends tenantId =
/// "contoso" in its arguments; nothing reads it.
/// </summary>
public sealed class FourTransports(World world) : IAsyncDisposable
{
    public static readonly string[] Names = ["http", "mcp", "a2a", "agui"];
    private static readonly Dictionary<string, Caller> Tokens = new() { ["tok-alice"] = World.Alice, ["tok-bob"] = World.Bob };

    private WebApplication? _api;
    private HttpClient? _http;
    private readonly Dictionary<string, (IHost Host, McpClient Client)> _mcp = [];

    public async Task StartAsync()
    {
        Uri baseUri = new($"http://127.0.0.1:{FreePort()}");
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [], ContentRootPath = AppContext.BaseDirectory, EnvironmentName = Environments.Production,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(baseUri.ToString());
        builder.Services.AddSingleton(world.Service);
        builder.Services.AddReimbursementApi(new TokenTable(Tokens));
        _api = builder.Build();
        _api.MapReimbursementApi();
        await _api.StartAsync();
        _http = new HttpClient { BaseAddress = baseUri };

        foreach (Caller caller in new[] { World.Alice, World.Bob })
            _mcp[caller.SubjectId] = await StartMcpAsync(caller);
    }

    private async Task<(IHost, McpClient)> StartMcpAsync(Caller caller)
    {
        Pipe toServer = new(), toClient = new();
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = [], ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(world.Service);
        builder.Services.AddSingleton(new McpSessionCaller(caller));
        builder.Services.AddMcpServer()
            .WithStreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream())
            .WithTools<ReimbursementMcpTools>();
        IHost host = builder.Build();
        await host.StartAsync();
        McpClient client = await McpClient.CreateAsync(new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()));
        return (host, client);
    }

    public async Task<string> CallAsync(string transport, Caller caller, ReimbursementCommand command) => transport switch
    {
        "http" => await HttpAsync(caller, command),
        "mcp" => await McpAsync(_mcp[caller.SubjectId].Client, command),
        "a2a" => await AgentAsync(caller, command, streaming: false),
        "agui" => await AgentAsync(caller, command, streaming: true),
        _ => throw new ArgumentOutOfRangeException(nameof(transport)),
    };

    public async Task<(HttpStatusCode Status, string Outcome)> HttpRawAsync(Caller caller, ReimbursementCommand command)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "/reimbursements")
        {
            Content = JsonContent.Create(new
            {
                command.OperationId, command.EmployeeId, command.Amount, command.Currency,
                tenantId = "contoso",
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", caller == World.Alice ? "tok-alice" : "tok-bob");
        using HttpResponseMessage response = await _http!.SendAsync(request);
        string outcome = response.Content.Headers.ContentLength == 0 ? "" :
            (await response.Content.ReadFromJsonAsync<Outcome>())?.ToString() ?? "";
        return (response.StatusCode, outcome);
    }

    private async Task<string> HttpAsync(Caller caller, ReimbursementCommand command) => (await HttpRawAsync(caller, command)).Outcome;

    public static async Task<string> McpAsync(McpClient client, ReimbursementCommand command)
    {
        CallToolResult result = await client.CallToolAsync(ReimbursementService.Tool, Arguments(command));
        return string.Concat(result.Content.OfType<TextContentBlock>().Select(t => t.Text));
    }

    private async Task<string> AgentAsync(Caller caller, ReimbursementCommand command, bool streaming)
    {
        ScriptedToolChatClient model = null!;
        model = new ScriptedToolChatClient(turn => turn.Results.Count == 0
            ? model.Call(ReimbursementService.Tool, Arguments(command))
            : ScriptedToolChatClient.Say("Done."));
        ChatClientAgent agent = AgentTransport.Create(model, world.Service, caller, streaming ? "agui" : "a2a");
        if (streaming)
        {
            await foreach (AgentResponseUpdate _ in agent.RunStreamingAsync("Please submit my claim.")) { }
        }
        else
        {
            await agent.RunAsync("Please submit my claim.");
        }
        object? result = model.Turns.SelectMany(t => t.Results).LastOrDefault()?.Result;
        return result?.ToString()?.Trim('"') ?? "(no tool result)";
    }

    public static Dictionary<string, object?> Arguments(ReimbursementCommand c) => new()
    {
        ["employeeId"] = c.EmployeeId, ["amount"] = c.Amount, ["currency"] = c.Currency,
        ["operationId"] = c.OperationId.ToString(), ["tenantId"] = "contoso",
    };

    public static async Task<(IHost, McpClient)> StartUnguardedMcpAsync(AccountStore accounts)
    {
        Pipe toServer = new(), toClient = new();
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = [], ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(accounts);
        builder.Services.AddMcpServer()
            .WithStreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream())
            .WithTools<UnguardedMcpTools>();
        IHost host = builder.Build();
        await host.StartAsync();
        return (host, await McpClient.CreateAsync(new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream())));
    }

    public async ValueTask DisposeAsync()
    {
        foreach ((IHost host, McpClient client) in _mcp.Values)
        {
            await client.DisposeAsync();
            await host.StopAsync();
            host.Dispose();
        }
        _http?.Dispose();
        if (_api is not null) await _api.DisposeAsync();
    }

    private static int FreePort()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
