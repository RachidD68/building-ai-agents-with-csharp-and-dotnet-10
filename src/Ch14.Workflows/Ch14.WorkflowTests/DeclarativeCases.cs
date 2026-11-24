using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Declarative;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Ch14.WorkflowTests;

/// <summary>
/// The same kind of graph, declared in YAML and built by DeclarativeWorkflowBuilder.
/// Everything runs in process: an in-memory agent provider (the YAML declares no
/// agent) and, for the HTTP cases, two loopback listeners on 127.0.0.1.
/// </summary>
internal static class DeclarativeCases
{
    public const string TriageYaml = """
        kind: Workflow
        trigger:
          kind: OnConversationStart
          id: claim_triage
          actions:
            - kind: SetVariable
              id: set_route
              variable: Local.Route
              value: =If(Len(System.LastMessage.Text) > 20, "review", "return")
            - kind: SetVariable
              id: count_runs
              variable: Global.Runs
              value: =If(IsBlank(Global.Runs), 1, Global.Runs + 1)
            - kind: SendActivity
              id: send_route
              activity: "route: {Local.Route}, runs: {Global.Runs}"
        """;

    private const string LongClaim = "Two hotel nights for the partner summit";

    public static DeclarativeWorkflowOptions Options(IHttpRequestHandler? http = null) =>
        new(new InMemoryAgentProvider())
        {
            HttpRequestHandler = http,       // every declared HTTP call goes through code you chose
            MaximumCallDepth = 4,
            MaximumExpressionLength = 500,
        };

    public static Workflow Build(string yaml, DeclarativeWorkflowOptions? options = null)
        => DeclarativeWorkflowBuilder.Build<string>(new StringReader(yaml), options ?? Options());

    public static async Task RunAllAsync()
    {
        DeclaredRun first = await RunAsync(Build(TriageYaml), LongClaim);
        Harness.Check("YAML builds an ordinary Workflow; each declared action raises its events",
            first.Invoked.SequenceEqual(["set_route", "count_runs", "send_route"]) &&
            first.Completed.Count == 3);
        Harness.Check("the declared expression routes a long claim to review",
            first.Text.Contains("route: review, runs: 1") && (await RunAsync(Build(TriageYaml), "taxi")).Text.Contains("route: return"));

        Workflow shared = Build(TriageYaml);
        DeclaredRun a = await RunAsync(shared, LongClaim), b = await RunAsync(shared, LongClaim);
        // Declarative 1.20.0 carried Global.Runs into run 2 ("runs: 2"); 1.28.0 starts each run with Global blank.
        Harness.Check("reuse one built declarative workflow: Global.Runs does not carry into run 2 (Declarative 1.28.0)",
            a.Text.Contains("runs: 1") && b.Text.Contains("runs: 1"), $"{a.Text} / {b.Text}");
        DeclaredRun c = await RunAsync(Build(TriageYaml), LongClaim), d = await RunAsync(Build(TriageYaml), LongClaim);
        Harness.Check("build per run -> identical output twice", c.Text == d.Text && c.Text.Contains("runs: 1"));

        Harness.Check("restore: a fresh build resumed from the checkpoint after set_route finishes the run",
            await RestoreAsync() is { } restored && restored.Text.Contains("route: review, runs: 1") &&
            !restored.Invoked.Contains("set_route") && restored.Invoked.Contains("send_route"));

        // Declarative 1.20.0 refused the 57-character expression when the run evaluated it. 1.28.0 still passes the
        // limit to the Power Fx engine it creates, but the run evaluates the expression anyway.
        Workflow? tight = null;
        string? buildError = null;
        try { tight = Build(TriageYaml, new DeclarativeWorkflowOptions(new InMemoryAgentProvider()) { MaximumExpressionLength = 10 }); }
        catch (Exception ex) { buildError = Flatten(ex); }
        DeclaredRun tooLong = tight is null ? new DeclaredRun([], [], "", [buildError ?? "build failed"]) : await RunAsync(tight, LongClaim);
        Harness.Defect("MaximumExpressionLength 10 (Declarative 1.28.0): Build accepts the file and the run still evaluates the 57-character expression",
            tight is not null && tooLong.Errors.Count == 0 && tooLong.Text.Contains("route: review, runs: 1"));

        await HttpContractAsync();
    }

    /// <summary>Runs to the end, checkpointing; resumes a fresh build from the step after set_route.</summary>
    private static async Task<(string Text, List<string> Invoked)?> RestoreAsync()
    {
        CheckpointManager checkpoints = CheckpointManager.CreateInMemory();
        CheckpointInfo? afterRoute = null;
        bool routeDone = false;
        await using (StreamingRun run = await InProcessExecution.RunStreamingAsync(
            Build(TriageYaml), LongClaim, checkpoints, Guid.NewGuid().ToString()))
        {
            await foreach (WorkflowEvent evt in run.WatchStreamAsync(blockOnPendingRequest: false))
            {
                if (evt is DeclarativeActionCompletedEvent { ActionId: "set_route" }) routeDone = true;
                if (evt is SuperStepCompletedEvent step && routeDone && afterRoute is null)
                    afterRoute = step.CompletionInfo?.Checkpoint;
            }
        }
        if (afterRoute is null) return null;

        StringBuilder text = new();
        List<string> invoked = [];
        await using StreamingRun resumed = await InProcessExecution.ResumeStreamingAsync(Build(TriageYaml), afterRoute, checkpoints);
        await foreach (WorkflowEvent evt in resumed.WatchStreamAsync(blockOnPendingRequest: false))
        {
            if (evt is AgentResponseEvent response) text.Append(response.Data);
            if (evt is DeclarativeActionInvokedEvent started) invoked.Add(started.ActionId);
        }
        return (text.ToString(), invoked);
    }

    // ---------------------------------------------------------------
    // The redirect contract: a declared HTTP step may reach only the hosts
    // the host program allows, and a redirect never takes it elsewhere.
    // Declarative 1.28's DefaultHttpRequestHandler enforces both itself.
    // ---------------------------------------------------------------
    private static async Task HttpContractAsync()
    {
        // Two loopback listeners on different ports stand in for two hosts: the allow-list compares host and port.
        await using LoopbackServer elsewhere = LoopbackServer.Start(_ => "HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok");
        await using LoopbackServer policy = LoopbackServer.Start(_ =>
            $"HTTP/1.1 302 Found\r\nLocation: {elsewhere.BaseUrl}/collect\r\nContent-Length: 0\r\n\r\n");
        string policyHost = new Uri(policy.BaseUrl).Authority, elsewhereHost = new Uri(elsewhere.BaseUrl).Authority;
        string yaml = HttpYaml(policy.BaseUrl + "/limits");

        // A primary handler that follows redirects itself (the HttpClientHandler default) is refused before any request.
        DeclaredRun plain = await RunAsync(Build(yaml, Options(new DefaultHttpRequestHandler([policyHost], HttpHandlers.Factory, "default"))), "go");
        Harness.Check("DefaultHttpRequestHandler refuses a primary handler that follows redirects; nothing is sent",
            policy.Hits == 0 && elsewhere.Hits == 0 && plain.Errors.Any(e => e.Contains("must disable automatic redirects")));
        Console.WriteLine($"        refused with: {plain.Errors.FirstOrDefault()?.Split(" -> ").Last()}");

        DefaultHttpRequestHandler guarded = new([policyHost], HttpHandlers.Factory, HttpHandlers.Declarative);
        DeclaredRun blocked = await RunAsync(Build(yaml, Options(guarded)), "go");
        Harness.Check("allow-list: the 302 to an authority not on the list is refused, the HTTP step fails",
            policy.Hits == 1 && elsewhere.Hits == 0 && blocked.Errors.Count > 0 && blocked.Text.Length == 0);

        DeclaredRun refused = await RunAsync(Build(HttpYaml(elsewhere.BaseUrl + "/collect"), Options(guarded)), "go");
        Harness.Check("allow-list: a host not on the list is refused before any connection",
            elsewhere.Hits == 0 && refused.Errors.Any(e => e.Contains("not in the allowed hosts list")));
        Console.WriteLine($"        refused with: {refused.Errors.FirstOrDefault()?.Split(" -> ").Last()}");

        DeclaredRun portless = await RunAsync(Build(yaml, Options(new DefaultHttpRequestHandler(["127.0.0.1"], HttpHandlers.Factory, HttpHandlers.Declarative))), "go");
        Harness.Check("allow-list: an entry without a port does not admit 127.0.0.1 on another port",
            policy.Hits == 1 && portless.Errors.Any(e => e.Contains("not in the allowed hosts list")));

        DefaultHttpRequestHandler both = new([policyHost, elsewhereHost], HttpHandlers.Factory, HttpHandlers.Declarative);
        DeclaredRun followed = await RunAsync(Build(yaml, Options(both)), "go");
        Harness.Check("allow-list: a 302 to another listed authority is followed (each hop is checked)",
            policy.Hits == 2 && elsewhere.Hits == 1 && followed.Errors.Count == 0 && followed.Text.Contains("ok"),
            string.Join(" | ", followed.Errors));

        string? badEntries = null;
        foreach (string entry in new[] { "http://127.0.0.1", "*" })
        {
            try { _ = new DefaultHttpRequestHandler([entry], HttpHandlers.Factory, HttpHandlers.Declarative); }
            catch (ArgumentException ex) { badEntries ??= ex.Message; continue; }
            badEntries = null; break;
        }
        Harness.Check("allow-list entries are host[:port]: a scheme or a wildcard is refused when the handler is built",
            badEntries is not null);
    }

    private static string HttpYaml(string url) => $"""
        kind: Workflow
        trigger:
          kind: OnConversationStart
          id: fetch_limits
          actions:
            - kind: HttpRequestAction
              id: get_limits
              method: Get
              url: {url}
              response: Local.Limits
            - kind: SendActivity
              id: send_limits
              activity: "limits: {"{"}Local.Limits{"}"}"
        """;

    internal sealed record DeclaredRun(List<string> Invoked, List<string> Completed, string Text, List<string> Errors);


    private static async Task<DeclaredRun> RunAsync(Workflow workflow, string input)
    {
        List<string> invoked = [], completed = [], errors = [];
        StringBuilder text = new();
        try
        {
            await using StreamingRun run = await InProcessExecution.RunStreamingAsync(workflow, input, sessionId: Guid.NewGuid().ToString());
            await foreach (WorkflowEvent evt in run.WatchStreamAsync(blockOnPendingRequest: false))
            {
                switch (evt)
                {
                    case DeclarativeActionInvokedEvent started: invoked.Add(started.ActionId); break;
                    case DeclarativeActionCompletedEvent done: completed.Add(done.ActionId); break;
                    case AgentResponseEvent response: text.Append(response.Data); break;
                    case WorkflowErrorEvent error: errors.Add(Flatten(error.Data as Exception) ?? error.Data?.ToString() ?? "error"); break;
                }
            }
        }
        catch (Exception ex) { errors.Add(Flatten(ex)!); }
        return new DeclaredRun(invoked, completed, text.ToString(), errors);
    }

    private static string? Flatten(Exception? ex)
    {
        List<string> parts = [];
        for (Exception? e = ex; e is not null; e = e.InnerException) parts.Add($"{e.GetType().Name}: {e.Message}");
        return ex is null ? null : string.Join(" -> ", parts);
    }
}

/// <summary>
/// The host's HTTP seam for declared workflows. DefaultHttpRequestHandler 1.28 takes the
/// allow-list and an IHttpMessageHandlerFactory; it requires a primary handler with automatic
/// redirects and cookies off, and follows redirects itself, checking every hop against the list.
/// </summary>
internal static class HttpHandlers
{
    public const string Declarative = "declarative";

    public static readonly IHttpMessageHandlerFactory Factory = Build();

    private static IHttpMessageHandlerFactory Build()
    {
        ServiceCollection services = new();
        services.AddHttpClient(Declarative).ConfigurePrimaryHttpMessageHandler(() =>
            new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
        services.AddHttpClient("default");             // the default primary handler: follows redirects
        return services.BuildServiceProvider().GetRequiredService<IHttpMessageHandlerFactory>();
    }
}
/// <summary>
/// The YAML declares no agent, so the provider only keeps conversations in
/// memory. No hosted service and no preview package are involved.
/// </summary>
internal sealed class InMemoryAgentProvider : ResponseAgentProvider
{
    private readonly Dictionary<string, List<ChatMessage>> _conversations = [];

    public override Task<string> CreateConversationAsync(CancellationToken cancellationToken = default)
    {
        string id = Guid.NewGuid().ToString("N");
        lock (_conversations) _conversations[id] = [];
        return Task.FromResult(id);
    }

    public override Task<ChatMessage> CreateMessageAsync(string conversationId, ChatMessage conversationMessage, CancellationToken cancellationToken = default)
    {
        lock (_conversations) _conversations[conversationId].Add(conversationMessage);
        return Task.FromResult(conversationMessage);
    }

    public override Task<ChatMessage> GetMessageAsync(string conversationId, string messageId, CancellationToken cancellationToken = default)
    {
        lock (_conversations) return Task.FromResult(_conversations[conversationId].First(m => m.MessageId == messageId));
    }

    public override IAsyncEnumerable<AgentResponseUpdate> InvokeAgentAsync(string agentId, string? agentVersion, string? conversationId,
        IEnumerable<ChatMessage>? messages, IDictionary<string, object?>? inputArguments, CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"this test declares no agent ('{agentId}' was requested)");

    public override async IAsyncEnumerable<ChatMessage> GetMessagesAsync(string conversationId, int? limit = null, string? after = null,
        string? before = null, bool newestFirst = false, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChatMessage[] snapshot;
        lock (_conversations) snapshot = [.. _conversations[conversationId]];
        foreach (ChatMessage message in newestFirst ? snapshot.Reverse() : snapshot) yield return message;
        await Task.CompletedTask;
    }
}

/// <summary>A one-response-per-connection HTTP listener on 127.0.0.1 that counts requests.</summary>
internal sealed class LoopbackServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<string, string> _respond;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private int _hits;

    private LoopbackServer(Func<string, string> respond)
    {
        _respond = respond;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _loop = Task.Run(AcceptAsync);
    }

    public static LoopbackServer Start(Func<string, string> respond) => new(respond);

    public string BaseUrl => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
    public int Hits => Volatile.Read(ref _hits);

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (Exception) { return; }
            using (client)
            {
                NetworkStream stream = client.GetStream();
                byte[] buffer = new byte[8192];
                StringBuilder head = new();
                while (!head.ToString().Contains("\r\n\r\n"))
                {
                    int read = await stream.ReadAsync(buffer, _stop.Token);
                    if (read == 0) break;
                    head.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }
                Interlocked.Increment(ref _hits);
                await stream.WriteAsync(Encoding.ASCII.GetBytes(_respond(head.ToString().Split(' ').ElementAtOrDefault(1) ?? "/")), _stop.Token);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try { await _loop; } catch (OperationCanceledException) { }
        _stop.Dispose();
    }
}
