using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Ch19.Observability;

/// <summary>
/// The Contoso reimbursement assistant with telemetry on both layers: the chat
/// client (one chat span per model call) and the agent (the run and its tool calls).
/// Sensitive data is set to false explicitly on both, because the default is
/// not a constant: Microsoft.Extensions.AI reads it from an environment variable.
/// </summary>
public static class ContosoAssistant
{
    public const string AgentName = "ContosoAssistant";
    public const string AgentId = "contoso-assistant";

    public static AIAgent Create(IChatClient model, ContosoTelemetry telemetry,
        ILoggerFactory? loggers = null, bool sensitiveData = false)
    {
        IChatClient chat = model.AsBuilder()
            .UseOpenTelemetry(loggers, configure: c => c.EnableSensitiveData = sensitiveData)
            .Build();

        PolicyTools tools = new(telemetry);
        ChatClientAgent agent = new(chat, new ChatClientAgentOptions
        {
            Id = AgentId,   // the span name carries the id: a fixed one keeps it a fixed name
            Name = AgentName,
            ChatOptions = new()
            {
                Instructions = "You answer Contoso reimbursement questions. Always call " +
                               "get_reimbursement_limit for a limit and quote its amount exactly.",
                Tools = [AIFunctionFactory.Create(tools.GetReimbursementLimit, "get_reimbursement_limit")],
            },
        });

        return agent.AsBuilder()
            .UseOpenTelemetry(configure: a => a.EnableSensitiveData = sensitiveData)
            .Build();
    }

    /// <summary>One request: a span of our own, bounded metrics, an ILogger scope.</summary>
    public static async Task<AgentResponse> AskAsync(AIAgent agent, ContosoTelemetry telemetry, ILogger logger,
        string conversationId, string question, CancellationToken ct = default)
    {
        using Activity? request = telemetry.StartRequest(conversationId);
        using IDisposable? scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["contoso.conversation_id"] = conversationId,   // logs are access-controlled, like traces
        });
        Stopwatch clock = Stopwatch.StartNew();
        try
        {
            AgentResponse response = await agent.RunAsync(question, await agent.CreateSessionAsync(ct), cancellationToken: ct);
            telemetry.RecordRequest("ok", clock.Elapsed);
            logger.LogInformation("Answered in {ElapsedMs} ms, {OutputTokens} output tokens",
                clock.ElapsedMilliseconds, response.Usage?.OutputTokenCount);   // sizes, never the text
            return response;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            telemetry.RecordRequest("failed", clock.Elapsed);
            request?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
            logger.LogError("Request failed: {ErrorType}", ex.GetType().Name);   // the type, not the message
            throw;
        }
    }
}

/// <summary>The reimbursement-limit tool. Its result is policy data, not telemetry.</summary>
public sealed class PolicyTools(ContosoTelemetry telemetry)
{
    public static readonly IReadOnlyDictionary<string, string> Limits = new Dictionary<string, string>
    {
        ["meals"] = "75 CAD per day",
        ["client_meals"] = "120 CAD per person",   // client dinners are their own category
        ["lodging"] = "300 CAD per night",
        ["travel"] = "2000 CAD per trip",
    };

    public Func<string, string>? Fault { get; set; }   // the self-test injects failures here

    [Description("Returns the reimbursement limit for a category: meals, client_meals (a meal with a client), lodging or travel.")]
    public string GetReimbursementLimit([Description("The expense category")] string category)
    {
        try
        {
            string result = Fault?.Invoke(category)
                ?? (Limits.TryGetValue(category.ToLowerInvariant(), out string? limit)
                    ? $"{category}: {limit}" : $"no limit is defined for '{category}'");
            telemetry.RecordToolCall("get_reimbursement_limit", failed: false);
            return result;
        }
        catch
        {
            telemetry.RecordToolCall("get_reimbursement_limit", failed: true);
            throw;
        }
    }
}
