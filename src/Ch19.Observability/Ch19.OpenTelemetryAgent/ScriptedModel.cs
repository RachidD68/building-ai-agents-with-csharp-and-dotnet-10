using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Ch19.Observability;

/// <summary>
/// A scripted model for the offline checks. A question about a limit becomes a
/// get_reimbursement_limit call; after the tool result it answers by quoting the
/// result (or, with <see cref="WrongAmount"/> set, by quoting a wrong amount).
/// It reports usage and metadata the way a provider connector does, so the
/// OpenTelemetry layer has something real to record.
/// </summary>
public sealed class ScriptedModel : IChatClient
{
    private int _calls;

    public int Calls => _calls;

    public string? WrongAmount { get; set; }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        List<ChatMessage> list = [.. messages];
        ChatMessage reply;
        if (list[^1].Contents.OfType<FunctionResultContent>().LastOrDefault() is { } result)
        {
            string text = result.Exception is not null || result.Result?.ToString() is not { } r || r.StartsWith("Error", StringComparison.Ordinal)
                ? "I could not look up that limit right now."
                : $"The limit is {WrongAmount ?? r.Split(": ", 2)[^1]}.";
            reply = new ChatMessage(ChatRole.Assistant, text);
        }
        else
        {
            string user = list.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";
            string category = user.Contains("hotel", StringComparison.OrdinalIgnoreCase) ? "lodging" : "meals";
            reply = new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent($"call-{_calls}", "get_reimbursement_limit",
                    new Dictionary<string, object?> { ["category"] = category })]);
        }

        return Task.FromResult(new ChatResponse(reply)
        {
            ModelId = "scripted-model",
            FinishReason = reply.Contents.OfType<FunctionCallContent>().Any() ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop,
            Usage = new UsageDetails { InputTokenCount = 40 * list.Count, OutputTokenCount = 12, TotalTokenCount = 40 * list.Count + 12 },
        });
    }

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
