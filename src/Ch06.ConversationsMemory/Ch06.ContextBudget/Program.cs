// ============================================================================
// Context budget: keeping the prompt under a token budget
// ----------------------------------------------------------------------------
// With no reduction, every turn sends the whole conversation again, so the
// input tokens of each call grow with the conversation. A reducer or the
// compaction pipeline holds them to a budget. Strategies, default-first:
//
//   MessageCountingChatReducer   built in, count-based           [MEAI001]
//   SummarizingChatReducer       built in, model-written summary [MEAI001]
//   compaction pipeline          CompactionProvider + strategies [MAAI001]
//   LastTurnsChatReducer         hand-written IChatReducer        GA
//
//   dotnet run                    the strategies against the configured model
//                                 (Ollama llama3.2), with the input tokens the
//                                 provider reports for each call
//   dotnet run -- --self-test     a fake client that reports UsageDetails;
//                                 20 turns per strategy; RESULT n/n
// Absorbs the first edition's Ch06.HistoryReduction.
// ============================================================================

using Ch06.ContextBudget;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgenticAI.Common.Extensions;

if (args.Contains("--self-test"))
    return await BudgetSuite.RunAsync();

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);
using IHost host = builder.Build();
IChatClient model = host.Services.GetRequiredService<IChatClient>();

Console.WriteLine("=== Context budget: the strategies on a real model ===");
string[] turns =
[
    "My employee id is E-1001 and I am filing travel expenses for a trip to Lyon.",
    "The hotel was two nights at 118 EUR a night.",
    "The train from Paris to Lyon cost 74 EUR.",
    "I also took a taxi from the station, 23.50 EUR.",
    "Please keep answers short.",
    "What is my employee id, and what was the hotel's nightly rate?",
];
foreach (Strategy strategy in (Strategy[])[Strategies.Unbounded, Strategies.MessageCounting,
             Strategies.Summarizing, Strategies.Compaction, Strategies.LastTurns])
{
    (ChatClientAgent agent, InMemoryChatHistoryProvider history) = strategy.Build(model);
    AgentSession session = await agent.CreateSessionAsync();
    List<string> tokens = [];
    AgentResponse? last = null;
    foreach (string turn in turns)
    {
        last = await agent.RunAsync(turn, session);
        tokens.Add(last.Usage?.InputTokenCount?.ToString() ?? "n/a");
    }
    Console.WriteLine($"\n--- {strategy.Name} [{strategy.Status}] ---");
    Console.WriteLine($"  input tokens per call (reported by the provider): {string.Join(", ", tokens)}");
    Console.WriteLine($"  stored messages after {turns.Length} turns: {history.GetMessages(session).Count}");
    Console.WriteLine($"  last question: {turns[^1]}");
    Console.WriteLine($"  answer: {last!.Text.ReplaceLineEndings(" ")}");
}
return 0;

internal static class BudgetSuite
{
    static int s_passed, s_total;

    public static async Task<int> RunAsync()
    {
        Console.WriteLine("=== Context budget: self-test (fake client, tokens estimated as bytes / 4) ===");
        // Twenty turns of about the same size, so growth comes from history alone.
        string[] turns = [.. Enumerable.Range(1, 20).Select(i =>
            $"Turn {i:00}: add line {i:00} to the Lyon claim: hotel, train or taxi, receipt R-{1000 + i}, " +
            $"amount {10 + i}.50 EUR, paid by card, business purpose: customer workshop day {i:00}.")];

        var results = new Dictionary<string, (long[] Tokens, List<UsageReportingChatClient.Call> Calls, int Stored)>();
        foreach (Strategy strategy in (Strategy[])[Strategies.Unbounded, Strategies.MessageCounting,
                     Strategies.Compaction, Strategies.LastTurns])
        {
            var fake = new UsageReportingChatClient();
            (ChatClientAgent agent, InMemoryChatHistoryProvider history) = strategy.Build(fake);
            AgentSession session = await agent.CreateSessionAsync();
            List<long> tokens = [];
            foreach (string turn in turns)
            {
                AgentResponse response = await agent.RunAsync(turn, session);
                tokens.Add(response.Usage?.InputTokenCount ?? -1);   // what the agent reports back
            }
            results[strategy.Name] = ([.. tokens], fake.Calls, history.GetMessages(session).Count);
            Console.WriteLine($"\n{strategy.Name} [{strategy.Status}]");
            Console.WriteLine($"  input tokens per turn: {string.Join(" ", tokens)}");
            Console.WriteLine($"  stored messages after 20 turns: {history.GetMessages(session).Count}");
        }

        Console.WriteLine();
        (long[] none, var noneCalls, int noneStored) = results[Strategies.Unbounded.Name];
        Expect("the usage the fake reported reaches AgentResponse.Usage.InputTokenCount",
            none.SequenceEqual(noneCalls.Select(c => c.InputTokens)));
        Expect($"no reduction: input tokens rise on every turn ({none[0]} -> {none[^1]})",
            none.Zip(none.Skip(1)).All(p => p.Second > p.First));
        Expect("no reduction: the 20th call costs more than 10x the first", none[^1] > 10 * none[0]);
        Expect($"no reduction: all 40 messages stored ({noneStored})", noneStored == 40);

        foreach (Strategy s in (Strategy[])[Strategies.MessageCounting, Strategies.Compaction, Strategies.LastTurns])
        {
            (long[] t, var calls, int stored) = results[s.Name];
            long[] tail = t[^10..];
            Expect($"{s.Name}: the last 10 turns stay within {tail.Max() - tail.Min()} tokens of each other (plateau)",
                tail.Max() - tail.Min() <= none[0]);
            Expect($"{s.Name}: turn 20 costs {t[^1]} tokens instead of {none[^1]}", t[^1] * 2 < none[^1]);
            Expect($"{s.Name}: every call still carried the current user line and the instructions",
                calls.Select(c => c.LastUserText).SequenceEqual(turns) && calls.All(c => c.HadInstructions));
            Console.WriteLine($"  ({s.Name}: {stored} messages stored)");
        }
        long[] compacted = results[Strategies.Compaction.Name].Tokens;
        int firstCompacted = Enumerable.Range(0, compacted.Length).First(i => compacted[i] < none[i]) + 1;
        Expect($"compaction pipeline: first compacted at turn {firstCompacted}; no call over the " +
               $"{Strategies.BudgetTokens}-token budget (max {compacted.Max()})",
            compacted.Max() <= Strategies.BudgetTokens);

        Console.WriteLine($"\nRESULT {s_passed}/{s_total}");
        return s_passed == s_total ? 0 : 1;
    }

    static void Expect(string name, bool ok)
    {
        s_total++;
        if (ok) s_passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    }
}
