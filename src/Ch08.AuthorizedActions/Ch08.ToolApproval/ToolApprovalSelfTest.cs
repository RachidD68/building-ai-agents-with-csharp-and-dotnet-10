using System.Runtime.CompilerServices;
using System.Text.Json;
using Ch08.ActionPolicy;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Ch08.ToolApproval;

/// <summary>
/// Offline check: dotnet run -- --self-test. Three suites, one RESULT line:
/// the per-order ledger, the invariant-culture amount rule (under de-DE), and
/// the real ToolApprovalAgent approval loop driven by a scripted model.
/// </summary>
internal static class ToolApprovalSelfTest
{
    static int s_passed, s_total;

    public static async Task<int> RunAsync()
    {
        Console.WriteLine("=== Tool approval: self-test ===");

        Console.WriteLine("\n-- Per-order ledger --");
        Add(RefundLedger.SelfTest());

        Console.WriteLine("\n-- Amount rule, read under de-DE --");
        Add(RefundRule.SelfTest());

        Console.WriteLine("\n-- Approval loop: ActionPolicy before approval (scripted model) --");
        await ApprovalLoopSuiteAsync();

        Console.WriteLine($"\nRESULT {s_passed}/{s_total}");
        return s_passed == s_total ? 0 : 1;
    }

    static async Task ApprovalLoopSuiteAsync()
    {
        var ledger = new RefundLedger();
        RefundGate gate = OrderAdmin.CreateGate(ledger);
        AIAgent agent = OrderAdmin.CreateAgent(new ScriptedChatClient(), gate, out Func<int> statusLookups);

        int reviews = 0;
        string reviewer = "rita", role = Roles.FinanceReviewer;
        bool reviewerApproves = true;
        ReviewerDecision Review(FunctionCallContent _)
        {
            reviews++;
            return new(reviewer, role, reviewerApproves);
        }

        async Task<(string Text, List<string> Log)> Run(string prompt)
        {
            List<string> log = [];
            AgentSession session = await agent.CreateSessionAsync();
            AgentResponse final = await ApprovalLoop.RunAsync(agent, session, prompt, gate, Review, log.Add, default);
            foreach (string line in log) Console.WriteLine($"    {line.Trim()}");
            return (final.Text, log);
        }

        var (_, statusLog) = await Run("status ORD-3003");
        Expect("get_order_status runs unattended (auto-approved, no request surfaced)",
            statusLookups() == 1 && statusLog.Count == 0);

        var (lowText, lowLog) = await Run("refund ORD-1001 20 USD");
        Expect("refund of 20 USD is auto-approved by rule + policy: one credit, no reviewer",
            lowLog.Count == 0 && ledger.CreditsIssued == 1 && reviews == 0 && lowText.Contains("Refund processed"));

        var (highText, highLog) = await Run("refund ORD-2002 80 USD");
        Expect("refund of 80 USD surfaces a request; policy says RequireApproval; reviewer asked once",
            highLog.Count == 1 && highLog[0].Contains("RequireApproval/approval_missing") && reviews == 1);
        Expect("after the reviewer approves, the tool runs and issues one credit",
            ledger.CreditsIssued == 2 && highText.Contains("Refund processed for ORD-2002"));

        var (replayText, replayLog) = await Run("refund ORD-2002 80 USD");
        Expect("the same refund again: the stored approval still matches, reviewer not asked",
            replayLog.Count == 1 && replayLog[0].Contains("Allow/exact_proposal_approved") && reviews == 1);
        Expect("... and the ledger issues no second credit (approval is not idempotency)",
            ledger.CreditsIssued == 2 && replayText.Contains("no second credit"));

        var (changedText, changedLog) = await Run("refund ORD-2002 95 USD");
        Expect("approval reused with a changed amount: Deny approval_invalid, never offered to the reviewer",
            changedLog.Count == 1 && changedLog[0].Contains("Deny/approval_invalid") && reviews == 1);
        Expect("... and the tool never ran", ledger.CreditsIssued == 2 && !changedText.Contains("Refund processed"));

        var (_, negativeLog) = await Run("refund ORD-1001 -10 USD");
        Expect("refund of -10 USD: Deny invalid_amount before any reviewer, no credit",
            negativeLog.Count == 1 && negativeLog[0].Contains("Deny/invalid_amount") && reviews == 1 && ledger.CreditsIssued == 2);

        var (_, currencyLog) = await Run("refund ORD-3003 30 EUR");
        Expect("refund in EUR on a USD order: Deny currency_mismatch, not auto-approved",
            currencyLog.Count == 1 && currencyLog[0].Contains("Deny/currency_mismatch") && ledger.CreditsIssued == 2);

        reviewer = "ann";      // the requester tries to approve their own request
        var (_, selfLog) = await Run("refund ORD-4004 300 USD");
        Expect("requester approving their own refund is rejected (separation of duties): no credit",
            selfLog.Count == 1 && selfLog[0].Contains("REJECTED") && reviews == 2 && ledger.CreditsIssued == 2 &&
            gate.Check(ScriptedChatClient.Call("refund ORD-4004 300 USD")!).Reason == "separation_of_duties");

        reviewer = "rita";
        reviewerApproves = false;
        var (rejectedText, rejectedLog) = await Run("refund ORD-3003 60 USD");
        Expect("reviewer rejects: the tool never runs, no credit",
            rejectedLog.Count == 1 && rejectedLog[0].Contains("REJECTED") && reviews == 3 &&
            ledger.CreditsIssued == 2 && !rejectedText.Contains("Refund processed"));
    }

    static void Add((int Passed, int Total) suite)
    {
        s_passed += suite.Passed;
        s_total += suite.Total;
    }

    static void Expect(string name, bool ok)
    {
        s_total++;
        if (ok) s_passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    }
}

/// <summary>
/// A scripted model for the self-test. A user line such as
/// "refund ORD-2002 80 USD" or "status ORD-3003" becomes one tool call with
/// JSON arguments, the way a real model's call arrives; after a tool result
/// (or a rejection) it answers with text that quotes the result.
/// </summary>
internal sealed class ScriptedChatClient : IChatClient
{
    private int _calls;

    public static FunctionCallContent? Call(string userText, string callId = "call-0")
    {
        string[] w = userText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return w switch
        {
            ["status", var order] => new FunctionCallContent(callId, "get_order_status", Args($$"""{"orderId":"{{order}}"}""")),
            ["refund", var order, var amount, var currency] => new FunctionCallContent(callId, "refund_order",
                Args($$"""{"orderId":"{{order}}","amount":{{amount}},"currency":"{{currency}}"}""")),
            _ => null,
        };
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        List<ChatMessage> list = [.. messages];
        if (list[^1].Contents.OfType<FunctionResultContent>().LastOrDefault() is { } result)
            return Reply(new ChatMessage(ChatRole.Assistant, $"Tool result: {result.Result}"));

        string userText = list.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";
        FunctionCallContent? call = Call(userText, $"call-{Interlocked.Increment(ref _calls)}");
        return Reply(call is null
            ? new ChatMessage(ChatRole.Assistant, "No tool needed.")
            : new ChatMessage(ChatRole.Assistant, [call]));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (ChatResponseUpdate update in response.ToChatResponseUpdates()) yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }

    private static Task<ChatResponse> Reply(ChatMessage message) => Task.FromResult(new ChatResponse(message));

    private static Dictionary<string, object?> Args(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!
            .ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
}
