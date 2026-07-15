// ============================================================================
// Chapter 8 — Tool approval (human-in-the-loop)
// ----------------------------------------------------------------------------
// Some tools are too dangerous to fire on the model's say-so alone. Refunding
// an order, deleting a record, sending money — you want a human (or a policy)
// to approve the call before it runs. MAF 1.13 gives you two layers for this:
//
//   1. ApprovalRequiredAIFunction (Microsoft.Extensions.AI) — wrap any
//      AIFunction so that, instead of executing, the run PAUSES and returns a
//      ToolApprovalRequestContent. You inspect the requested call, decide, and
//      RESUME the run by sending back a ToolApprovalResponseContent.
//
//   2. ToolApprovalAgent (Microsoft.Agents.AI, new in 1.10) — a delegating
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
// ============================================================================

using System.ComponentModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgenticAI.Common.Extensions;

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
        // AutoApprovalRules is an IEnumerable<Func<FunctionCallContent, ValueTask<bool>>>.
        // Each rule sees the pending call and returns true to wave it through.
        AutoApprovalRules =
        [
            (FunctionCallContent call) =>
                ValueTask.FromResult(call.Name == "get_order_status"),
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

[Description("Refunds the specified order in full and returns a confirmation. This is a sensitive write operation.")]
static string RefundOrder(
    [Description("The order identifier to refund, e.g. 'ORD-1001'.")] string orderId)
{
    // In a real system this would call the payments service.
    return $"Refund processed for {orderId}. A credit of $129.00 was issued to the original payment method.";
}

[Description("Gets the current status of an order. Read-only and safe to call without approval.")]
static string GetOrderStatus(
    [Description("The order identifier to look up, e.g. 'ORD-3003'.")] string orderId)
{
    return $"Order {orderId}: shipped on 2026-04-12, expected delivery 2026-04-16.";
}
