using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using AgenticAI.Common.Extensions;
using Ch04.FirstAgent;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Ch04.MinimalApiAgent;

/// <summary>The size and time budget of one request.</summary>
public sealed record AgentApiLimits(int MaxMessageLength = 2000, int MaxOutputTokens = 400, double TimeBudgetSeconds = 60);

/// <summary>JSON body of POST /chat.</summary>
public sealed record ChatRequest(string? Message);

/// <summary>JSON reply of POST /chat. Single-turn: no request sees another's history.</summary>
public sealed record ChatReply(string Agent, string Message, string Status, bool SingleTurn);

/// <summary>Every finished run, for the server log and for the self-test.</summary>
public sealed class RunLog(ILogger<RunLog> logger)
{
    // Only the self-test reads this, so it is bounded: a server nobody reads keeps the last 100.
    private readonly Channel<RunOutcome> _ended = Channel.CreateBounded<RunOutcome>(
        new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.DropOldest });

    public void Add(string route, RunOutcome outcome)
    {
        logger.LogInformation("{Route} ended {Status} after {Length} characters {Error}",
            route, outcome.Status, outcome.Text.Length, outcome.Error?.GetType().Name);
        _ended.Writer.TryWrite(outcome);
    }

    public ValueTask<RunOutcome> NextAsync(CancellationToken cancellationToken) => _ended.Reader.ReadAsync(cancellationToken);
}

public static class AgentApi
{
    /// <summary>
    /// Builds the app. <paramref name="model"/> replaces the configured provider
    /// (the self-test passes a fake); <paramref name="configure"/> adjusts the builder.
    /// </summary>
    public static WebApplication Build(string[] args, IChatClient? model = null, AgentApiLimits? limits = null,
        Action<WebApplicationBuilder>? configure = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        // No retry in this file: the HTTP resilience handler that AddLlmChatClient
        // installs is the one retry owner (the SDK's own retries are set to 0).
        if (model is null) builder.Services.AddLlmChatClient(builder.Configuration);
        else builder.Services.AddSingleton(model);
        builder.Services.AddSingleton(limits ?? new AgentApiLimits());
        builder.Services.AddSingleton(sp => ContosoPolicy.Create(sp.GetRequiredService<IChatClient>()));
        builder.Services.AddSingleton<RunLog>();
        configure?.Invoke(builder);

        WebApplication app = builder.Build();
        app.MapPost("/chat", ChatAsync);
        app.MapGet("/chat/stream", Stream);
        app.MapGet("/", () => TypedResults.Ok(new
        {
            service = "Contoso expense help",
            conversation = "single-turn: every request starts with no history",
            chat = "POST /chat with { \"message\": \"...\" }",
            stream = "GET /chat/stream?message=... (server-sent events; the last event is 'done')",
        }));
        return app;
    }

    // POST /chat: validation first, then one run inside the time budget.
    private static async Task<IResult> ChatAsync(
        ChatRequest request, ChatClientAgent agent, AgentApiLimits limits, RunLog log, CancellationToken requestAborted)
    {
        if (Invalid(request.Message, limits) is { } problem) return problem;

        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        budget.CancelAfter(TimeSpan.FromSeconds(limits.TimeBudgetSeconds));
        ChatClientAgentRunOptions options = new(new ChatOptions { MaxOutputTokens = limits.MaxOutputTokens });

        // No session: a throwaway one per request, so the endpoint is single-turn.
        RunOutcome outcome = await AgentRuns.RunAsync(
            () => agent.RunAsync(request.Message!, session: null, options, budget.Token), requestAborted, budget.Token);
        log.Add("POST /chat", outcome);

        return outcome.Status switch
        {
            RunStatus.Completed or RunStatus.Truncated =>
                TypedResults.Ok(new ChatReply(agent.Name!, outcome.Text, Wire(outcome.Status), SingleTurn: true)),
            RunStatus.Cancelled => TypedResults.StatusCode(499),          // the client went away
            _ when budget.IsCancellationRequested =>                      // our token, not the provider's
                TypedResults.Problem("The model did not answer within the time budget.",
                    statusCode: StatusCodes.Status504GatewayTimeout, title: "time_budget"),
            _ when outcome.FinishReason == ChatFinishReason.ContentFilter =>
                TypedResults.Problem("The provider withheld the answer (content filter).",
                    statusCode: StatusCodes.Status502BadGateway, title: "content_filter"),
            _ => TypedResults.Problem("The model call failed.",          // the type name stays in the log
                    statusCode: StatusCodes.Status502BadGateway, title: "provider_error"),
        };
    }

    // GET /chat/stream: the same validation, then server-sent events ending with 'done'.
    private static IResult Stream(
        string? message, ChatClientAgent agent, AgentApiLimits limits, RunLog log, HttpContext http)
    {
        if (Invalid(message, limits) is { } problem) return problem;
        return TypedResults.ServerSentEvents(Events(message!, agent, limits, log, http.RequestAborted));
    }

    private static async IAsyncEnumerable<SseItem<string>> Events(
        string message, ChatClientAgent agent, AgentApiLimits limits, RunLog log,
        [EnumeratorCancellation] CancellationToken requestAborted)
    {
        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        budget.CancelAfter(TimeSpan.FromSeconds(limits.TimeBudgetSeconds));
        ChatClientAgentRunOptions options = new(new ChatOptions { MaxOutputTokens = limits.MaxOutputTokens });
        RunTracker tracker = new(requestAborted, budget.Token);
        try
        {
            await using IAsyncEnumerator<AgentResponseUpdate> updates =
                agent.RunStreamingAsync(message, options: options, cancellationToken: budget.Token).GetAsyncEnumerator();
            while (await tracker.NextAsync(updates) is { } update)
            {
                if (update.Text is { Length: > 0 } text) yield return new SseItem<string>(text, "delta");
            }
        }
        finally
        {
            // A client that disconnects can stop the enumeration before the run ends.
            log.Add("GET /chat/stream", tracker.HasEnded ? tracker.Outcome
                : new RunOutcome(RunStatus.Cancelled, "", null, null));
        }
        RunOutcome outcome = tracker.Outcome;
        string reason = outcome.Status != RunStatus.Failed ? ""
                      : budget.IsCancellationRequested ? "time_budget"
                      : outcome.FinishReason == ChatFinishReason.ContentFilter ? "content_filter"
                      : "provider_error";
        yield return new SseItem<string>(
            JsonSerializer.Serialize(new { status = Wire(outcome.Status), reason }), "done");
    }

    private static IResult? Invalid(string? message, AgentApiLimits limits) =>
        string.IsNullOrWhiteSpace(message)
            ? TypedResults.Problem("A message is required.", statusCode: StatusCodes.Status400BadRequest, title: "validation_error")
        : message.Length > limits.MaxMessageLength
            ? TypedResults.Problem($"A message is at most {limits.MaxMessageLength} characters.",
                statusCode: StatusCodes.Status400BadRequest, title: "validation_error")
        : null;

    private static string Wire(RunStatus status) => status.ToString().ToLowerInvariant();
}
