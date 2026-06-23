// ============================================================================
// Chapter 6 — Conversations and Memory (History Reduction)
// Keeping conversation history under the model's context window as it grows.
//
// Four approaches, default-first:
//   1. MessageCountingChatReducer  — built-in, count-based trimming   [MEAI001]
//   2. SummarizingChatReducer      — built-in, LLM summarization      [MEAI001]
//   3. The compaction pipeline     — production-grade, composable     [MAAI001]
//   4. A hand-rolled IChatReducer  — the escape hatch, GA contract
//
// A reducer plugs into InMemoryChatHistoryProviderOptions.ChatReducer. By
// default it runs BeforeMessagesRetrieval — the provider applies the reducer
// when the conversation is retrieved and KEEPS the reduced view, so the
// running history converges to the window size instead of growing without
// bound (GetMessages returns the reduced set).
// ============================================================================

using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgenticAI.Common.Extensions;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);

using IHost host = builder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();

Console.WriteLine("=== Chapter 6: Keeping History Under the Window ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// A handful of short turns we can replay against each strategy so the effect
// of reduction is visible without burning a real conversation.
string[] turns =
[
    "My name is Rachid and I am building an agent in C#.",
    "I live in Seattle.",
    "My favourite framework is the Microsoft Agent Framework.",
    "I prefer concise answers.",
    "What is my name and where do I live?"
];

try
{
    // ------------------------------------------------------------------
    // 1) MessageCountingChatReducer — the default, count-based trim.
    //    Keeps the first system message + the most recent N non-system
    //    messages. Function call/result pairs are dropped from the
    //    reduced view, so a tool call is never orphaned from its result.
    // ------------------------------------------------------------------
    Console.WriteLine("--- 1. MessageCountingChatReducer(targetCount: 4) ---");

    InMemoryChatHistoryProvider countingProvider = new(new InMemoryChatHistoryProviderOptions
    {
        ChatReducer = new MessageCountingChatReducer(targetCount: 4)
        // ReducerTriggerEvent defaults to BeforeMessagesRetrieval.
    });

    await RunConversationAsync(
        chatClient, countingProvider, "CountingBot", turns, cts.Token);

    // ------------------------------------------------------------------
    // 2) SummarizingChatReducer — replace older turns with an LLM summary.
    //    Preserves context at lower token cost. Costs one extra (cheap)
    //    model call whenever it fires.
    // ------------------------------------------------------------------
    Console.WriteLine("\n--- 2. SummarizingChatReducer(targetCount: 4) ---");

    InMemoryChatHistoryProvider summarizingProvider = new(new InMemoryChatHistoryProviderOptions
    {
        ChatReducer = new SummarizingChatReducer(
            chatClient,
            targetCount: 4,
            threshold: 2)
    });

    await RunConversationAsync(
        chatClient, summarizingProvider, "SummaryBot", turns, cts.Token);

    // ------------------------------------------------------------------
    // 3) The compaction pipeline — the production-grade option.
    //    A PipelineCompactionStrategy composes smaller strategies, each
    //    gated by a CompactionTrigger. Here: evict old tool results first,
    //    then truncate the oldest turns once we exceed a turn budget.
    //    AsChatReducer() bridges the pipeline into the same ChatReducer slot.
    // ------------------------------------------------------------------
    Console.WriteLine("\n--- 3. PipelineCompactionStrategy via AsChatReducer() ---");

    CompactionStrategy pipeline = new PipelineCompactionStrategy(
    [
        // Collapse old tool-call/result groups when any are present.
        new ToolResultCompactionStrategy(
            trigger: CompactionTriggers.HasToolCalls(),
            minimumPreservedGroups: 2),

        // Drop the oldest turns once the conversation exceeds 2 user turns,
        // but never go below the 2 most-recent turns.
        new TruncationCompactionStrategy(
            trigger: CompactionTriggers.TurnsExceed(2),
            minimumPreservedGroups: 2)
    ]);

    InMemoryChatHistoryProvider pipelineProvider = new(new InMemoryChatHistoryProviderOptions
    {
        ChatReducer = pipeline.AsChatReducer()
    });

    await RunConversationAsync(
        chatClient, pipelineProvider, "PipelineBot", turns, cts.Token);

    // The same pipeline can run as an in-run context provider instead of a
    // reducer, via CompactionProvider on ChatClientAgentOptions.AIContextProviders:
    //
    //   AIContextProviders = [new CompactionProvider(pipeline)]
    //
    // Use the provider when you want compaction to happen inside the tool
    // loop (before every model call); use AsChatReducer() when you only need
    // to trim what the history provider hands back.

    // ------------------------------------------------------------------
    // 4) The escape hatch — a hand-rolled IChatReducer.
    //    Only when the built-ins don't fit. This one keeps the system
    //    message and the last N messages, written against the GA contract.
    // ------------------------------------------------------------------
    Console.WriteLine("\n--- 4. Custom LastNChatReducer(keep: 4) ---");

    InMemoryChatHistoryProvider customProvider = new(new InMemoryChatHistoryProviderOptions
    {
        ChatReducer = new LastNChatReducer(keep: 4)
    });

    await RunConversationAsync(
        chatClient, customProvider, "CustomBot", turns, cts.Token);
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n[Cancelled by user]");
}

Console.WriteLine("\n=== History Reduction demo complete! ===");

// ----------------------------------------------------------------------
// Helper: run the canned turns against one agent and report what survived
// in the reduced view that the model actually saw.
// ----------------------------------------------------------------------
static async Task RunConversationAsync(
    IChatClient chatClient,
    InMemoryChatHistoryProvider historyProvider,
    string name,
    string[] turns,
    CancellationToken ct)
{
    ChatClientAgent agent = new(chatClient, new ChatClientAgentOptions
    {
        Name = name,
        ChatHistoryProvider = historyProvider,
        ChatOptions = new ChatOptions { Instructions = "You are a helpful assistant. Be concise." }
    });

    AgentSession session = await agent.CreateSessionAsync(ct);

    foreach (string turn in turns)
    {
        AgentResponse response = await agent.RunAsync(turn, session, cancellationToken: ct);

        // Only print the answer to the final, recall question to keep output tight.
        if (turn == turns[^1])
        {
            Console.WriteLine($"  Final question: {turn}");
            Console.WriteLine($"  Answer:         {response.Text}");
        }
    }

    // With the default BeforeMessagesRetrieval trigger the provider keeps the
    // reduced view, so GetMessages returns the trimmed set — five turns (ten
    // raw messages) converge to the reducer's target rather than growing.
    List<ChatMessage> stored = historyProvider.GetMessages(session);
    Console.WriteLine($"  Stored messages after {turns.Length} turns: {stored.Count}");
}

// ----------------------------------------------------------------------
// A minimal custom reducer. IChatReducer has a single method:
//   Task<IEnumerable<ChatMessage>> ReduceAsync(IEnumerable<ChatMessage>, CancellationToken)
// Keep it pure: no per-session instance fields. The history provider owns
// per-session state in the AgentSession; the reducer is shared.
// ----------------------------------------------------------------------
internal sealed class LastNChatReducer(int keep) : IChatReducer
{
    public Task<IEnumerable<ChatMessage>> ReduceAsync(
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        List<ChatMessage> all = messages.ToList();

        // Always keep a leading system message if one is present.
        List<ChatMessage> system = all.Count > 0 && all[0].Role == ChatRole.System
            ? [all[0]]
            : [];

        List<ChatMessage> rest = all.Skip(system.Count).ToList();
        IEnumerable<ChatMessage> tail = rest.Count <= keep
            ? rest
            : rest.Skip(rest.Count - keep);

        return Task.FromResult<IEnumerable<ChatMessage>>([.. system, .. tail]);
    }
}
