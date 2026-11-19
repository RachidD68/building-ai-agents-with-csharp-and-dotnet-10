// ============================================================================
// Chapter 8 — Tool approval (human-in-the-loop)
// ----------------------------------------------------------------------------
// Some tools are too dangerous to fire on the model's say-so alone. Refunding
// an order, deleting a record, sending money — you want a human (or a policy)
// to approve the call before it runs. MAF 1.28 gives you two layers for this:
//
//   1. ApprovalRequiredAIFunction (Microsoft.Extensions.AI) — wrap any
//      AIFunction so that, instead of executing, the run PAUSES and returns a
//      ToolApprovalRequestContent. You inspect the requested call, decide, and
//      RESUME the run by sending back a ToolApprovalResponseContent.
//
//   2. ToolApprovalAgent (Microsoft.Agents.AI, stable in 1.28) — a delegating
//      agent that sits in front of your agent and applies "don't ask again"
//      rules plus auto-approval heuristics, so trusted calls flow through
//      without a prompt while sensitive ones still pause.
//
// This sample shows BOTH: a manual approve path, a manual reject path, and an
// auto-approval rule that waves a read-only tool through untouched.
//
// All-or-nothing gotcha: when ANY tool in a model response requires approval,
// the function-invoking client converts ALL the tool calls in that response
// into approval requests. We set ChatOptions.AllowMultipleToolCalls = false so
// the model emits one call at a time and each approval decision is isolated.
//
// Approval is not idempotency. An approved call can still run twice — a retry
// after a timeout, a resumed session, a double-clicked button. So the refund
// tool is idempotent on its business key (the order id): refunding the same
// order again returns the original confirmation and issues no second credit.
// Offline check of that ledger: dotnet run -- --self-test
// ============================================================================

using System.ComponentModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgenticAI.Common.Extensions;

if (args.Contains("--self-test")) return RefundLedger.SelfTest();

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);

using IHost host = builder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine("=== Chapter 8: Tool Approval (human-in-the-loop) ===\n");

// ── Tools ───────────────────────────────────────────────────────────
// One sensitive write tool (RefundOrder) and one harmless read tool
// (GetOrderStatus). We will gate the write tool behind approval.
// Name the tools explicitly so the schema the model sees is "refund_order"
// and "get_order_status" — not the compiler-generated local-function name.
AIFunction refundFn = AIFunctionFactory.Create(RefundOrder, name: "refund_order");
AIFunction statusFn = AIFunctionFactory.Create(GetOrderStatus, name: "get_order_status");

// Wrap the sensitive tool. The run will pause on a call to this one.
AIFunction gatedRefund = new ApprovalRequiredAIFunction(refundFn);

// Instructions, tools, and AllowMultipleToolCalls all live on ChatOptions.
// Forcing one tool call per turn keeps each approval decision isolated — see
// the all-or-nothing note in the header.
ChatOptions chatOptions = new()
{
    Instructions =
        "You are an order-administration assistant for Contoso. " +
        "Use refund_order to refund an order and get_order_status to check status. " +
        "Always call the tool; do not claim an action without calling its tool.",
    Tools = [gatedRefund, statusFn],
    AllowMultipleToolCalls = false,
};

ChatClientAgent agent = new(
    chatClient,
    new ChatClientAgentOptions
    {
        Name = "OrderAdmin",
        ChatOptions = chatOptions,
    });

// ── Demo 1: the APPROVED path ───────────────────────────────────────
Console.WriteLine("--- Demo 1: refund, approved ---");
await RunWithApprovalAsync(
    agent,
    "Please refund order ORD-1001.",
    approve: true,
    cts.Token);

// ── Demo 2: the REJECTED path ───────────────────────────────────────
Console.WriteLine("\n--- Demo 2: refund, rejected ---");
await RunWithApprovalAsync(
    agent,
    "Please refund order ORD-2002.",
    approve: false,
    cts.Token);

// ── Demo 3: auto-approval with ToolApprovalAgent ────────────────────
// Wrap the agent in a ToolApprovalAgent whose auto-approval rule waves
// through any call whose tool name is "get_order_status". Sensitive calls
// (refund_order) would still pause — but this prompt only needs the read.
Console.WriteLine("\n--- Demo 3: auto-approval rule (read-only tool) ---");

AIAgent autoAgent = agent.AsBuilder()
    .UseToolApproval(new ToolApprovalAgentOptions
    {
        // AutoApprovalRules is an IEnumerable<Func<ToolAutoApprovalRuleContext, ValueTask<bool>>>.
        // Each rule sees the run's context — the pending call is context.FunctionCallContent —
        // and returns true to wave it through.
        AutoApprovalRules =
        [
            (ToolAutoApprovalRuleContext context) =>
                ValueTask.FromResult(context.FunctionCallContent.Name == "get_order_status"),
        ],
    })
    .Build();

AgentSession autoSession = await autoAgent.CreateSessionAsync(cts.Token);
AgentResponse autoResponse = await autoAgent.RunAsync(
    "What is the status of order ORD-3003?",
    autoSession,
    cancellationToken: cts.Token);
Console.WriteLine($"Agent: {autoResponse.Text}\n");

Console.WriteLine("=== Tool approval demo complete! ===");
return 0;

// ----------------------------------------------------------------------------
// Drives one approve/reject loop against an agent that has a gated tool.
// 1) First run pauses with a ToolApprovalRequestContent.
// 2) We build a ToolApprovalResponseContent via request.CreateResponse(...).
// 3) Second run resumes with that response and produces the final answer.
// ----------------------------------------------------------------------------
static async Task RunWithApprovalAsync(
    AIAgent agent,
    string prompt,
    bool approve,
    CancellationToken ct)
{
    AgentSession session = await agent.CreateSessionAsync(ct);
    Console.WriteLine($"User: {prompt}");

    AgentResponse response = await agent.RunAsync(prompt, session, cancellationToken: ct);

    // Did the run pause for approval?
    List<ToolApprovalRequestContent> approvals = response.Messages
        .SelectMany(m => m.Contents)
        .OfType<ToolApprovalRequestContent>()
        .ToList();

    if (approvals.Count == 0)
    {
        // No tool needed approval — the model answered (or used a free tool).
        Console.WriteLine($"Agent: {response.Text}\n");
        return;
    }

    // Build a response for each pending approval request.
    List<AIContent> decisions = [];
    foreach (ToolApprovalRequestContent request in approvals)
    {
        // request.ToolCall is a ToolCallContent; the concrete type is
        // FunctionCallContent, which carries the name and arguments.
        FunctionCallContent? call = request.ToolCall as FunctionCallContent;
        Console.WriteLine(
            $"  [approval requested] tool='{call?.Name}' " +
            $"args={FormatArgs(call)} -> {(approve ? "APPROVE" : "REJECT")}");

        decisions.Add(request.CreateResponse(
            approved: approve,
            reason: approve ? "Authorized by admin." : "Denied by admin."));
    }

    // Resume: send the decision(s) back in the SAME session.
    ChatMessage decisionMessage = new(ChatRole.User, decisions);
    AgentResponse final = await agent.RunAsync(decisionMessage, session, cancellationToken: ct);

    Console.WriteLine($"Agent: {final.Text}\n");
}

static string FormatArgs(FunctionCallContent? call)
{
    if (call?.Arguments is null || call.Arguments.Count == 0)
    {
        return "{}";
    }
    return "{ " + string.Join(", ", call.Arguments.Select(a => $"{a.Key}={a.Value}")) + " }";
}

// ----------------------------------------------------------------------------
// Tools
// ----------------------------------------------------------------------------

[Description("Refunds the specified order in full and returns a confirmation. This is a sensitive write operation. Idempotent per order: refunding the same order again returns the original confirmation and issues no second credit.")]
static string RefundOrder(
    [Description("The order identifier to refund, e.g. 'ORD-1001'.")] string orderId)
    => RefundLedger.Refund(orderId);

[Description("Gets the current status of an order. Read-only and safe to call without approval.")]
static string GetOrderStatus(
    [Description("The order identifier to look up, e.g. 'ORD-3003'.")] string orderId)
{
    return $"Order {orderId}: shipped on 2026-04-12, expected delivery 2026-04-16.";
}

// ----------------------------------------------------------------------------
// The refund ledger: one credit per order, however many times the call runs.
// In a real system this is a UNIQUE constraint on the order id in the payments
// database (and the payment provider's own idempotency key), not a dictionary.
// ----------------------------------------------------------------------------
internal static class RefundLedger
{
    static readonly object Gate = new();
    static readonly Dictionary<string, string> Confirmations = new(StringComparer.OrdinalIgnoreCase);
    static int creditsIssued;

    public static int CreditsIssued => creditsIssued;

    public static string Refund(string orderId)
    {
        string key = orderId.Trim();
        // The check and the credit happen under one lock, so two concurrent
        // calls for the same order can never both issue a credit.
        lock (Gate)
        {
            if (Confirmations.TryGetValue(key, out string? earlier))
                return $"{earlier} (Already refunded earlier; no second credit was issued.)";

            // In a real system this would call the payments service.
            string confirmation = $"Refund processed for {key}. A credit of $129.00 was issued to the original payment method.";
            creditsIssued++;
            Confirmations[key] = confirmation;
            return confirmation;
        }
    }

    /// <summary>Offline check: dotnet run -- --self-test</summary>
    public static int SelfTest()
    {
        int passed = 0, total = 0;
        void Check(string name, bool ok)
        {
            total++; passed += ok ? 1 : 0;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
        }

        string first = Refund("ORD-1001");
        string again = Refund("ORD-1001");
        string sameIdOtherCase = Refund(" ord-1001 ");
        Check("first refund issues a credit", first.StartsWith("Refund processed") && !first.Contains("Already"));
        Check("second refund of the same order issues no credit", again.Contains("no second credit"));
        Check("order id is matched case-insensitively and trimmed", sameIdOtherCase.Contains("no second credit"));
        Check("exactly one credit for ORD-1001", CreditsIssued == 1);

        Parallel.For(0, 50, _ => Refund("ORD-2002"));
        Check("50 concurrent refunds of ORD-2002 issue one credit", CreditsIssued == 2);

        Refund("ORD-3003");
        Check("a different order gets its own credit", CreditsIssued == 3);

        Console.WriteLine($"RESULT {passed}/{total}");
        return passed == total ? 0 : 1;
    }
}
