using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Declarative;
using Microsoft.Extensions.AI;

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
        Harness.Defect("reuse one built declarative workflow: Global.Runs carries into run 2 (Declarative 1.20.0)",
            a.Text.Contains("runs: 1") && b.Text.Contains("runs: 2"));
        DeclaredRun c = await RunAsync(Build(TriageYaml), LongClaim), d = await RunAsync(Build(TriageYaml), LongClaim);
        Harness.Check("fixed: build per run -> identical output twice", c.Text == d.Text && c.Text.Contains("runs: 1"));

        Harness.Check("restore: a fresh build resumed from the checkpoint after set_route finishes the run",
            await RestoreAsync() is { } restored && restored.Text.Contains("route: review, runs: 1") &&
            !restored.Invoked.Contains("set_route") && restored.Invoked.Contains("send_route"));

        // Build and run apart: the limit is enforced when the run evaluates the expression, not by Build.
        Workflow? tight = null;
        string? buildError = null;
        try { tight = Build(TriageYaml, new DeclarativeWorkflowOptions(new InMemoryAgentProvider()) { MaximumExpressionLength = 10 }); }
        catch (Exception ex) { buildError = Flatten(ex); }
        DeclaredRun tooLong = tight is null ? new DeclaredRun([], [], "", [buildError ?? "build failed"]) : await RunAsync(tight, LongClaim);
        Harness.Check("MaximumExpressionLength 10: Build accepts the file; the run refuses the expression, nothing is sent",
            tight is not null && tooLong.Text.Length == 0 && tooLong.Errors.Count > 0, string.Join(" | ", tooLong.Errors));
        Console.WriteLine($"        refused with: {tooLong.Errors.FirstOrDefault()?.Split(" -> ").Last()}");

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
    // ---------------------------------------------------------------
    private static async Task HttpContractAsync()
    {
        // Two loopback listeners on different ports stand in for two hosts: the allow-list compares authorities.
        await using LoopbackServer elsewhere = LoopbackServer.Start(_ => "HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok");
        await using LoopbackServer policy = LoopbackServer.Start(_ =>
            $"HTTP/1.1 302 Found\r\nLocation: {elsewhere.BaseUrl}/collect\r\nContent-Length: 0\r\n\r\n");
        string yaml = HttpYaml(policy.BaseUrl + "/limits");

        await using (DefaultHttpRequestHandler plain = new())
            await RunAsync(Build(yaml, Options(plain)), "go");
        Harness.Defect("DefaultHttpRequestHandler on a default HttpClient follows a 302 to another authority (host:port)",
            policy.Hits == 1 && elsewhere.Hits == 1);

        await using AllowListHttpRequestHandler guarded = new([new Uri(policy.BaseUrl).Authority]);
        DeclaredRun blocked = await RunAsync(Build(yaml, Options(guarded)), "go");
        Harness.Check("allow-list handler: the redirect is not followed, the HTTP step fails",
            policy.Hits == 2 && elsewhere.Hits == 1 && blocked.Errors.Count > 0 && blocked.Text.Length == 0);

        DeclaredRun refused = await RunAsync(Build(HttpYaml(elsewhere.BaseUrl + "/collect"), Options(guarded)), "go");
        Harness.Check("allow-list handler: a host not on the list is refused before any connection",
            elsewhere.Hits == 1 && refused.Errors.Any(e => e.Contains("not on the allow-list")));
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
/// The host's HTTP seam for declared workflows: only allow-listed authorities,
/// and redirects are returned, never followed.
/// </summary>
internal sealed class AllowListHttpRequestHandler : IHttpRequestHandler, IAsyncDisposable
{
    private readonly HashSet<string> _allowed;
    private readonly HttpClient _client = new(new SocketsHttpHandler { AllowAutoRedirect = false });
    private readonly DefaultHttpRequestHandler _inner;

    public AllowListHttpRequestHandler(IEnumerable<string> allowedAuthorities)
    {
        _allowed = new HashSet<string>(allowedAuthorities, StringComparer.OrdinalIgnoreCase);
        _inner = new DefaultHttpRequestHandler(_client);
    }

    public Task<HttpRequestResult> SendAsync(HttpRequestInfo request, CancellationToken cancellationToken = default)
    {
        Uri uri = new(request.Url);
        if (!_allowed.Contains(uri.Authority))
            throw new InvalidOperationException($"refused: {uri.Authority} is not on the allow-list");
        return _inner.SendAsync(request, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync();
        _client.Dispose();
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
