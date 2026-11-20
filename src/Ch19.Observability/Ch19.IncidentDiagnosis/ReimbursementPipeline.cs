using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Ch19.Observability;

public enum Fault { None, EmptyRetrieval, ToolException, WrongValue }

/// <summary>
/// The Contoso reimbursement question, end to end: search_policy (retrieval),
/// get_reimbursement_limit (tool), then the answer. Each step leaves a signal
/// on a span, and no signal carries document text, prompts or tool results.
/// </summary>
public sealed partial class ReimbursementPipeline(ContosoTelemetry telemetry, ILoggerFactory loggers, Fault fault)
{
    public const string AgentName = "ContosoAssistant";
    public const string ToolFailureMessage = "limits service did not answer";

    // Policy passages. The second sentence of each is the kind of text that
    // must never reach telemetry; the self-test searches for it.
    private static readonly (string Id, string Text)[] Passages =
    [
        ("policy-client-meals", "Dinners with clients are category client_meals. Exception file CONF-2208 (R. Osei) approved a 180 CAD dinner."),
        ("policy-meals", "Meals while travelling are category meals. Exception file CONF-3119 (M. Diallo) covers a dietary allowance."),
    ];

    public static readonly IReadOnlyDictionary<string, string> Limits = new Dictionary<string, string>
    {
        ["meals"] = "75 CAD per day",
        ["client_meals"] = "120 CAD per person",
    };

    public ScriptedReimbursementModel Model { get; } = new() { WrongAmount = fault == Fault.WrongValue ? "150 CAD" : null };

    public async Task<string> AskAsync(string conversationId, string question)
    {
        IChatClient chat = Model.AsBuilder()
            .UseOpenTelemetry(loggers, configure: c => c.EnableSensitiveData = false)
            .Build();
        AIAgent agent = new ChatClientAgent(chat, new ChatClientAgentOptions
        {
            Id = "contoso-assistant",
            Name = AgentName,
            ChatOptions = new()
            {
                Instructions = "Answer reimbursement questions from the policy. Search first, then look up the limit.",
                Tools =
                [
                    AIFunctionFactory.Create(SearchPolicy, "search_policy"),
                    AIFunctionFactory.Create(GetReimbursementLimit, "get_reimbursement_limit"),
                ],
            },
        }).AsBuilder().UseOpenTelemetry(configure: a => a.EnableSensitiveData = false).Build();

        using Activity? request = telemetry.StartRequest(conversationId);
        Stopwatch clock = Stopwatch.StartNew();
        AgentResponse response = await agent.RunAsync(question, await agent.CreateSessionAsync());
        CheckAnswer(response);
        telemetry.RecordRequest(_outcome, clock.Elapsed);
        return response.Text;
    }

    private string _outcome = "ok";

    [Description("Searches the reimbursement policy. Returns matching passages.")]
    private string SearchPolicy([Description("What to search for")] string query)
    {
        using Activity? step = telemetry.Source.StartActivity("contoso.retrieval");
        (string Id, string Text)[] hits = fault == Fault.EmptyRetrieval
            ? []
            : [.. Passages.Where(p => query.Contains("client", StringComparison.OrdinalIgnoreCase) == p.Id.Contains("client"))];
        step?.SetTag("contoso.retrieval.hits", hits.Length);                        // a count, not text
        step?.SetTag("contoso.retrieval.doc_ids", string.Join(",", hits.Select(h => h.Id)));
        if (hits.Length == 0) _outcome = "no_evidence";
        return hits.Length == 0 ? "no passages found" : string.Join("\n", hits.Select(h => h.Text));
    }

    [Description("Returns the reimbursement limit for a policy category.")]
    private string GetReimbursementLimit([Description("The policy category")] string category)
    {
        if (fault == Fault.ToolException)
        {
            telemetry.RecordToolCall("get_reimbursement_limit", failed: true);
            _outcome = "tool_error";
            throw new TimeoutException(ToolFailureMessage);
        }
        telemetry.RecordToolCall("get_reimbursement_limit", failed: false);
        return Limits.TryGetValue(category, out string? limit) ? $"{category}: {limit}" : $"unknown category {category}";
    }

    /// <summary>
    /// Is every amount in the answer the amount the tool returned? The check
    /// records the verdict and the reason, never the two strings it compared.
    /// </summary>
    private void CheckAnswer(AgentResponse response)
    {
        using Activity? step = telemetry.Source.StartActivity("contoso.answer.check");
        string? toolResult = response.Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>()
            .Where(r => r.CallId.StartsWith("limit", StringComparison.Ordinal))
            .Select(r => r.Result?.ToString()).LastOrDefault();
        string[] answered = [.. Amount().Matches(response.Text).Select(m => m.Value)];
        string[] expected = toolResult is null ? [] : [.. Amount().Matches(toolResult).Select(m => m.Value)];

        string reason = expected.Length == 0 ? "no_tool_amount"
            : answered.Length == 0 ? "no_amount_in_answer"
            : answered.All(expected.Contains) ? "matches_tool" : "amount_mismatch";
        step?.SetTag("contoso.answer.grounded", reason == "matches_tool");
        step?.SetTag("contoso.answer.reason", reason);
    }

    [GeneratedRegex(@"\d+ CAD")]
    private static partial Regex Amount();
}

/// <summary>
/// The scripted model: search, then look up the category the passages name
/// (or plain "meals" when nothing came back), then quote the limit. After a
/// failed lookup it guesses, as small models do.
/// </summary>
public sealed class ScriptedReimbursementModel : IChatClient
{
    public string? WrongAmount { get; init; }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        List<ChatMessage> list = [.. messages];
        FunctionResultContent? last = list[^1].Contents.OfType<FunctionResultContent>().LastOrDefault();
        ChatMessage reply = last switch
        {
            null => Call("search-1", "search_policy", "query", list.Last(m => m.Role == ChatRole.User).Text),
            { CallId: "search-1" } => Call("limit-1", "get_reimbursement_limit", "category",
                last.Result?.ToString()?.Contains("client_meals") == true ? "client_meals" : "meals"),
            _ when last.Exception is not null || last.Result?.ToString()?.StartsWith("Error", StringComparison.Ordinal) == true
                => new ChatMessage(ChatRole.Assistant, "The limit is 75 CAD per day."),
            _ => new ChatMessage(ChatRole.Assistant, $"The limit is {WrongAmount ?? last.Result?.ToString()?.Split(": ", 2)[^1]}."),
        };
        return Task.FromResult(new ChatResponse(reply)
        {
            ModelId = "scripted-model",
            Usage = new UsageDetails { InputTokenCount = 60 * list.Count, OutputTokenCount = 10 },
        });
    }

    private static ChatMessage Call(string id, string name, string arg, string value) =>
        new(ChatRole.Assistant, [new FunctionCallContent(id, name, new Dictionary<string, object?> { [arg] = value })]);

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (ChatResponseUpdate update in response.ToChatResponseUpdates()) yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata("scripted", null, "scripted-model")
        : serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }
}
