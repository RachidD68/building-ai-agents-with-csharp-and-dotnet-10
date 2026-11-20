using AgenticAI.Testing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Ch20.MessageInjector;

/// <summary>
/// The real ChatClientAgent loop (function invocation, context provider,
/// message injection) over a scripted model. Each model call is recorded,
/// so the test reads exactly what the model was sent.
/// </summary>
internal static class InjectorSelfTest
{
    static int s_passed, s_total;

    public static async Task<int> RunAsync()
    {
        Console.WriteLine("=== Safety context: self-test ===");

        Console.WriteLine("\n-- Mid-run: a tool result with instructions --");
        {
            (ChatClientAgent agent, ScriptedToolChatClient model, SafetyContextProvider provider) = New();
            await agent.RunAsync("What does the travel policy say? Look it up.", await agent.CreateSessionAsync());
            IReadOnlyList<ScriptedTurn> t = model.Turns;
            Expect($"one run, {t.Count} model calls (tool call, then answer)", t.Count == 2);
            Expect("model call 1 carries no safety note", !Sent(t[0]).Contains(PolicyDocuments.SafetyNote));
            Expect("model call 2 carries the safety note the tool enqueued", Sent(t[1]).Contains(PolicyDocuments.SafetyNote));
            Expect($"DEFECT REPRODUCED: the provider ran {provider.Runs} time(s) for {t.Count} model calls " +
                "(the first edition said: before every model invocation)", provider.Runs == 1);
        }
        {
            (ChatClientAgent agent, ScriptedToolChatClient model, _) = New();
            await agent.RunAsync("What does the returns policy say? Look it up.", await agent.CreateSessionAsync());
            Expect("a clean document adds no note", model.Turns.Count == 2 &&
                model.Turns.All(turn => !Sent(turn).Contains(PolicyDocuments.SafetyNote)));
        }

        Console.WriteLine("\n-- Per run: instructions for the request in hand --");
        {
            (ChatClientAgent agent, ScriptedToolChatClient model, _) = New();
            await agent.RunAsync("Please reimburse employee E-2002 45 EUR.", await agent.CreateSessionAsync());
            await agent.RunAsync("What is the dress code?", await agent.CreateSessionAsync());
            Expect("an action request gets the action notice", Sent(model.Turns[0]).Contains(SafetyContextProvider.ActionNotice));
            Expect("a question does not", !Sent(model.Turns[1]).Contains(SafetyContextProvider.ActionNotice));
        }

        Console.WriteLine("\n-- Concurrent runs share one provider instance --");
        {
            (ChatClientAgent agent, ScriptedToolChatClient model, SafetyContextProvider provider) = New();
            Task[] runs = [.. Enumerable.Range(0, 16).Select(i => Task.Run(async () =>
            {
                string prompt = i % 2 == 0 ? $"Please reimburse employee E-{1000 + i} 20 EUR." : $"What is rule {i}?";
                await agent.RunAsync(prompt, await agent.CreateSessionAsync());
            }))];
            await Task.WhenAll(runs);
            int wrong = model.Turns.Count(turn =>
                UserText(turn).Contains("reimburse", StringComparison.OrdinalIgnoreCase) !=
                Sent(turn).Contains(SafetyContextProvider.ActionNotice));
            Expect($"16 concurrent runs (Task.Run): {provider.Runs} provider runs, {wrong} with the wrong notice",
                provider.Runs == 16 && model.Turns.Count == 16 && wrong == 0);
        }
        {
            // The first edition's "combining the layers" shape: the caller sets
            // CurrentQuery on a singleton, then runs. Two requests interleave.
            FirstEditionQueryProvider shared = new();
            (ChatClientAgent agent, ScriptedToolChatClient model, _) = New(shared);
            shared.CurrentQuery = "Please reimburse employee E-1001 20 EUR.";   // request A
            shared.CurrentQuery = "What is the dress code?";                  // request B, before A runs
            await agent.RunAsync("Please reimburse employee E-1001 20 EUR.", await agent.CreateSessionAsync());
            Expect("DEFECT REPRODUCED: with a CurrentQuery singleton, request A runs without its notice",
                !Sent(model.Turns[0]).Contains(SafetyContextProvider.ActionNotice));
        }

        Console.WriteLine($"\nRESULT {s_passed}/{s_total}");
        return s_passed == s_total ? 0 : 1;
    }

    static (ChatClientAgent, ScriptedToolChatClient, SafetyContextProvider) New()
    {
        SafetyContextProvider provider = new();
        (ChatClientAgent agent, ScriptedToolChatClient model, _) = New(provider);
        return (agent, model, provider);
    }

    static (ChatClientAgent, ScriptedToolChatClient, AIContextProvider) New(AIContextProvider provider)
    {
        ScriptedToolChatClient model = null!;
        model = new ScriptedToolChatClient(turn =>
        {
            if (turn.Results.Count > 0) return ScriptedToolChatClient.Say("Here is what the policy says.");
            string user = UserText(turn);
            if (!user.Contains("Look it up", StringComparison.Ordinal)) return ScriptedToolChatClient.Say("ok");
            string topic = user.Contains("travel", StringComparison.Ordinal) ? "travel" : "returns";
            return model.Call("lookup_policy_document", new Dictionary<string, object?> { ["topic"] = topic });
        });
        return (SafetyAgent.Create(model, provider), model, provider);
    }

    /// <summary>Everything the model was sent on one call: instructions and every message.</summary>
    static string Sent(ScriptedTurn turn) =>
        (turn.Options?.Instructions ?? "") + "\n" + string.Join("\n", turn.Messages.Select(m => m.Text));

    static string UserText(ScriptedTurn turn) =>
        string.Join("\n", turn.Messages.Where(m => m.Role == ChatRole.User).Select(m => m.Text));

    static void Expect(string name, bool ok)
    {
        s_total++;
        if (ok) s_passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    }
}

/// <summary>The first edition's shared-field provider, kept only to reproduce its race.</summary>
internal sealed class FirstEditionQueryProvider : AIContextProvider
{
    public string CurrentQuery { get; set; } = "";

    protected override ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new AIContext
        {
            Instructions = CurrentQuery.Contains("reimburse", StringComparison.OrdinalIgnoreCase)
                ? SafetyContextProvider.ActionNotice : null,
        });
}
