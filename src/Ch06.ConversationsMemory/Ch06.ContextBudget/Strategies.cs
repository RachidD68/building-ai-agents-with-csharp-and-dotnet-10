using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;

namespace Ch06.ContextBudget;

/// <summary>One way of keeping history, and the agent that uses it.</summary>
public sealed record Strategy(string Name, string Status, Func<IChatClient, (ChatClientAgent Agent, InMemoryChatHistoryProvider History)> Build);

public static class Strategies
{
    public const string Instructions = "You are the Contoso expense assistant. Be concise.";

    /// <summary>The token budget the compaction pipeline holds the prompt to (estimated tokens).</summary>
    public const int BudgetTokens = 300;

    // No reduction at all: every turn sends the whole conversation again.
    public static readonly Strategy Unbounded = new("no reduction", "GA", client =>
        Agent(client, new InMemoryChatHistoryProvider()));

    // Built in, count-based. Keeps the most recent messages. [MEAI001]
    public static readonly Strategy MessageCounting = new("MessageCountingChatReducer(6)", "experimental MEAI001", client =>
        Agent(client, new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions
        {
            ChatReducer = new MessageCountingChatReducer(targetCount: 6),
        })));

    // Built in, replaces older turns with a model-written summary: one extra model
    // call whenever it fires, so it is only run against a real model. [MEAI001]
    public static readonly Strategy Summarizing = new("SummarizingChatReducer(4, 2)", "experimental MEAI001", client =>
        Agent(client, new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions
        {
            ChatReducer = new SummarizingChatReducer(client, targetCount: 4, threshold: 2),
        })));

    // The compaction pipeline, run before every model call by CompactionProvider.
    // Old tool results are collapsed first; then, once the prompt is over the
    // budget, the oldest turns slide out, never the last two. [MAAI001]
    public static readonly Strategy Compaction = new($"compaction pipeline (budget {BudgetTokens})", "experimental MAAI001", client =>
    {
        CompactionStrategy pipeline = new PipelineCompactionStrategy(
            new ToolResultCompactionStrategy(CompactionTriggers.HasToolCalls(), minimumPreservedGroups: 2),
            new SlidingWindowCompactionStrategy(CompactionTriggers.TokensExceed(BudgetTokens), minimumPreservedTurns: 2));
        return Agent(client, new InMemoryChatHistoryProvider(), new CompactionProvider(pipeline));
    });

    // The GA escape hatch: a hand-written IChatReducer that keeps the last turns.
    public static readonly Strategy LastTurns = new("LastTurnsChatReducer(3), hand-written", "GA", client =>
        Agent(client, new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions
        {
            ChatReducer = new LastTurnsChatReducer(keepTurns: 3),
        })));

    private static (ChatClientAgent, InMemoryChatHistoryProvider) Agent(
        IChatClient client, InMemoryChatHistoryProvider history, AIContextProvider? context = null) =>
        (new ChatClientAgent(client, new ChatClientAgentOptions
        {
            Name = "ExpenseAssistant",
            ChatHistoryProvider = history,
            AIContextProviders = context is null ? null : [context],
            ChatOptions = new ChatOptions { Instructions = Instructions },
        }), history);
}

/// <summary>
/// Keeps the system message and the last N user turns with their replies. A
/// turn is never cut in half, so a tool call is never separated from its result.
/// Pure: no per-session fields, because one reducer serves every session.
/// </summary>
public sealed class LastTurnsChatReducer(int keepTurns) : IChatReducer
{
    public Task<IEnumerable<ChatMessage>> ReduceAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken)
    {
        List<ChatMessage> all = [.. messages];
        List<ChatMessage> system = [.. all.Where(m => m.Role == ChatRole.System)];
        List<ChatMessage> rest = [.. all.Where(m => m.Role != ChatRole.System)];
        int[] turnStarts = [.. rest.Select((m, i) => (m, i)).Where(x => x.m.Role == ChatRole.User).Select(x => x.i)];
        int from = turnStarts.Length <= keepTurns ? 0 : turnStarts[^keepTurns];
        return Task.FromResult<IEnumerable<ChatMessage>>([.. system, .. rest.Skip(from)]);
    }
}

/// <summary>
/// A fake model for the self-test. It reports the input it was sent as
/// UsageDetails.InputTokenCount, estimated as UTF-8 bytes / 4 (the rule the
/// compaction index uses when it has no tokenizer), and records what it saw.
/// </summary>
public sealed class UsageReportingChatClient : IChatClient
{
    public sealed record Call(long InputTokens, int Messages, string? LastUserText, bool HadInstructions);

    public List<Call> Calls { get; } = [];

    public static long Estimate(IEnumerable<ChatMessage> messages, ChatOptions? options) =>
        (messages.Sum(m => Encoding.UTF8.GetByteCount(m.Text ?? "")) +
         Encoding.UTF8.GetByteCount(options?.Instructions ?? "")) / 4;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        List<ChatMessage> sent = [.. messages];
        long input = Estimate(sent, options);
        string? last = sent.LastOrDefault(m => m.Role == ChatRole.User)?.Text;
        bool instructions = options?.Instructions == Strategies.Instructions ||
                            sent.Any(m => m.Role == ChatRole.System && m.Text.Contains(Strategies.Instructions));
        lock (Calls) Calls.Add(new Call(input, sent.Count, last, instructions));
        var reply = new ChatMessage(ChatRole.Assistant, $"Noted ({Calls.Count}).");
        return Task.FromResult(new ChatResponse(reply)
        {
            Usage = new UsageDetails { InputTokenCount = input, OutputTokenCount = 4, TotalTokenCount = input + 4 },
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
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }
}
