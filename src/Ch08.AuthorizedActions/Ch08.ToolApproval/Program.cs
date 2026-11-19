// ============================================================================
// Tool approval, with the policy checked first
// ----------------------------------------------------------------------------
// Some tools must not fire on the model's say-so alone. Two framework layers:
//
//   1. ApprovalRequiredAIFunction (Microsoft.Extensions.AI) wraps an
//      AIFunction so that, instead of executing, the run PAUSES and returns a
//      ToolApprovalRequestContent. You decide, then RESUME the run on the same
//      session with request.CreateResponse(approved, reason).
//   2. ToolApprovalAgent (Microsoft.Agents.AI) sits in front of the agent and
//      applies AutoApprovalRules, so a trusted call flows through unattended
//      while a sensitive one still pauses.
//
// The application adds the part the framework cannot know: ActionPolicy runs
// BEFORE anyone is asked. A Deny (negative amount, wrong currency, unknown
// order, an approval reused with other arguments) is rejected without ever
// reaching a reviewer. RequireApproval goes to a reviewer, whose decision is
// stored server-side and bound to the exact arguments. Allow goes through.
//
// Approval is not idempotency. An approved call can still run twice (a retry
// after a timeout, a resumed session, a double click), so refund_order goes
// through a per-order ledger: one credit per order, however often it runs.
// Framework approval request ids and business operation ids are two ids with
// two jobs: the first pairs a response with its request, the second keys the
// effect.
//
// All-or-nothing: when ANY tool call in a response needs approval, every call
// in that response becomes an approval request. AllowMultipleToolCalls = false
// keeps one call, and one decision, per turn.
//
//   dotnet run                    four prompts against the configured model
//   dotnet run -- --self-test     offline: the ledger, the amount rule (de-DE)
//                                 and the approval loop with a scripted model
// ============================================================================

using System.ComponentModel;
using Ch08.ActionPolicy;
using Ch08.ToolApproval;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgenticAI.Common.Extensions;

if (args.Contains("--self-test")) return await ToolApprovalSelfTest.RunAsync();

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);
using IHost host = builder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine("=== Tool approval, policy first ===\n");

RefundGate gate = OrderAdmin.CreateGate(new RefundLedger());
AIAgent agent = OrderAdmin.CreateAgent(chatClient, gate, out _);

// A simulated reviewer: the server would authenticate this person first.
Func<FunctionCallContent, ReviewerDecision> approveAsRita = _ => new("rita", Roles.FinanceReviewer, Approved: true);

string[] prompts =
[
    "What is the status of order ORD-3003?",
    "Refund 20 USD on order ORD-1001.",
    "Refund 80 USD on order ORD-2002.",
    "Refund -10 USD on order ORD-1001.",
];
foreach (string prompt in prompts)
{
    Console.WriteLine($">> {prompt}");
    AgentSession session = await agent.CreateSessionAsync(cts.Token);
    AgentResponse final = await ApprovalLoop.RunAsync(
        agent, session, prompt, gate, approveAsRita, Console.WriteLine, cts.Token);
    Console.WriteLine($"<< {final.Text}\n");
}
Console.WriteLine($"Credits issued: {gate.Ledger.CreditsIssued}");
return 0;

/// <summary>The order-administration agent: one gated write, one read.</summary>
internal static class OrderAdmin
{
    public const string Tenant = "contoso";

    public static readonly Order[] Orders =
    [
        new("ORD-1001", "C-1001", 129.00m, "USD", 3),
        new("ORD-2002", "C-2002", 480.00m, "USD", 5),
        new("ORD-3003", "C-3003", 64.50m, "USD", 2),
        new("ORD-4004", "C-4004", 900.00m, "USD", 1),
    ];

    public static RefundGate CreateGate(RefundLedger ledger) =>
        new(Tenant, new Caller(Tenant, "ann", new HashSet<string> { Permissions.SubmitReimbursement }),
            ledger, Orders);

    public static AIAgent CreateAgent(IChatClient chatClient, RefundGate gate, out Func<int> statusLookups)
    {
        int lookups = 0;
        statusLookups = () => Volatile.Read(ref lookups);

        [Description("Refunds an amount on an order to the original payment method. Sensitive write: " +
                     "needs approval. Idempotent per order: a second refund of the same order issues no credit.")]
        string RefundOrder(
            [Description("The order id, e.g. 'ORD-1001'.")] string orderId,
            [Description("The amount to refund, e.g. 20.00.")] decimal amount,
            [Description("ISO currency code, e.g. 'USD'.")] string currency)
            => gate.Refund(orderId, amount, currency);

        [Description("Gets the current status of an order. Read-only.")]
        string GetOrderStatus([Description("The order id, e.g. 'ORD-3003'.")] string orderId)
        {
            Interlocked.Increment(ref lookups);
            return $"Order {orderId}: shipped, in transit.";
        }

        // Both tools are wrapped; the auto-approval rule below decides which
        // calls may skip the pause.
        AIFunction refund = new ApprovalRequiredAIFunction(
            AIFunctionFactory.Create((Delegate)RefundOrder, name: "refund_order"));
        AIFunction status = new ApprovalRequiredAIFunction(
            AIFunctionFactory.Create((Delegate)GetOrderStatus, name: "get_order_status"));

        ChatClientAgent inner = new(chatClient, new ChatClientAgentOptions
        {
            Name = "OrderAdmin",
            ChatOptions = new ChatOptions
            {
                Instructions =
                    "You are an order-administration assistant. Use refund_order to refund an amount on an " +
                    "order and get_order_status to check an order. Always call the tool; never claim an " +
                    "action without calling its tool.",
                Tools = [refund, status],
                AllowMultipleToolCalls = false,
            },
        });

        // Auto-approve the read-only lookup, and a refund only when the amount
        // rule (more than 0, at most the low-value limit, read in the
        // invariant culture) AND ActionPolicy both say yes.
        return inner.AsBuilder()
            .UseToolApproval(new ToolApprovalAgentOptions
            {
                AutoApprovalRules =
                [
                    (ToolAutoApprovalRuleContext context) => ValueTask.FromResult(
                        context.FunctionCallContent.Name switch
                        {
                            "get_order_status" => true,
                            "refund_order" =>
                                context.FunctionCallContent.Arguments is { } a &&
                                a.TryGetValue("amount", out object? raw) &&
                                RefundRule.ShouldAutoApprove(raw, out _) &&
                                gate.Check(context.FunctionCallContent).Decision == Decision.Allow,
                            _ => false,
                        }),
                ],
            })
            .Build();
    }
}

/// <summary>
/// Request, decide, resume: the loop every approval-gated run goes through.
/// The policy decides first; a reviewer sees only what the policy cannot settle.
/// </summary>
internal static class ApprovalLoop
{
    public const int MaxRounds = 5;

    public static async Task<AgentResponse> RunAsync(
        AIAgent agent, AgentSession session, string prompt, RefundGate gate,
        Func<FunctionCallContent, ReviewerDecision> reviewer, Action<string> log, CancellationToken ct)
    {
        AgentResponse response = await agent.RunAsync(prompt, session, cancellationToken: ct);
        for (int round = 1; response.Messages.SelectMany(m => m.Contents).OfType<ToolApprovalRequestContent>().ToList()
             is { Count: > 0 } requests; round++)
        {
            if (round > MaxRounds) throw new InvalidOperationException($"still asking for approval after {MaxRounds} rounds");
            List<AIContent> decisions = [];
            foreach (ToolApprovalRequestContent request in requests)
            {
                var call = (FunctionCallContent)request.ToolCall;
                PolicyResult policy = gate.Check(call);                 // ActionPolicy before any human
                bool approved = policy.Decision switch
                {
                    Decision.Allow => true,
                    Decision.RequireApproval => gate.RecordReview(call, reviewer),
                    _ => false,                                         // Deny: never offered to a reviewer
                };
                log($"   [approval] {call.Name}({FormatArgs(call)}) policy={policy.Decision}/{policy.Reason} " +
                    $"-> {(approved ? "APPROVED" : "REJECTED")}");
                decisions.Add(request.CreateResponse(approved, approved ? policy.Reason : $"denied: {policy.Reason}"));
            }
            // Resume on the SAME session with the decision(s).
            response = await agent.RunAsync(new ChatMessage(ChatRole.User, decisions), session, cancellationToken: ct);
        }
        return response;
    }

    private static string FormatArgs(FunctionCallContent call) =>
        call.Arguments is null ? "" : string.Join(", ", call.Arguments.Select(a => $"{a.Key}={a.Value}"));
}
