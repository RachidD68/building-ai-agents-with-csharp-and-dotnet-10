// ============================================================================
// Tool-call approval: gating Excessive Agency (OWASP LLM06)
// ----------------------------------------------------------------------------
// The middleware in Ch20.MiddlewarePipeline intercepts *messages*. It never
// gates *actions*. An agent holding a DeleteCustomerRecord / IssueRefund tool
// can be driven — directly or via indirect injection — to ACT. The MAF-native
// defence is human-in-the-loop on the tool itself:
//
//   1. Wrap the dangerous AIFunction in ApprovalRequiredAIFunction
//      (Microsoft.Extensions.AI). The inner agent then refuses to execute it;
//      instead it surfaces a ToolApprovalRequestContent.
//   2. Wrap the agent in a ToolApprovalAgent (Microsoft.Agents.AI). This
//      middleware queues approval requests, applies AutoApprovalRules
//      (argument validation / least-privilege heuristics), and surfaces the
//      rest to the caller one at a time.
//   3. The caller inspects each request, decides, and replies with
//      request.CreateResponse(approved: true/false, reason). Resume on the
//      SAME session. Approve -> the tool runs. Reject -> it never executes.
//
// Ch08.ToolApproval covers the per-function mechanics of tool approval. This
// project's angle is the SAFETY pattern: sensitive action => approval gate + argument validation +
// least privilege.
//
// NOTE: ToolApprovalAgent / ToolApprovalAgentOptions carried [Experimental
// ("MAAI001")] through MAF 1.13; they are GA in 1.14, so the old <NoWarn>
// suppression is gone from the .csproj. The ApprovalRequiredAIFunction /
// ToolApprovalRequestContent types in Microsoft.Extensions.AI are stable.
//
// Offline check of the refund rule: dotnet run -- --self-test
// ============================================================================

using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgenticAI.Common.Extensions;

if (args.Contains("--self-test")) return RefundRule.SelfTest();

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);

using IHost host = builder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();

Console.WriteLine("=== Tool-call Approval (Excessive Agency / LLM06) ===");
Console.WriteLine();

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// ── The dangerous tools ───────────────────────────────────────────────
[Description("Permanently deletes a customer record by id. Destructive and irreversible.")]
static string DeleteCustomerRecord(
    [Description("The customer id, e.g. C-1001")] string customerId)
    => $"Customer {customerId} permanently deleted.";

[Description("Issues a refund to a customer. customerId like C-1001, amount in USD.")]
static string IssueRefund(string customerId, decimal amount)
    => $"Refunded ${amount} to {customerId}.";

// Each is wrapped so the inner agent cannot execute it without approval.
AIFunction deleteTool = new ApprovalRequiredAIFunction(
    AIFunctionFactory.Create((Delegate)DeleteCustomerRecord, name: "delete_customer_record"));
AIFunction refundTool = new ApprovalRequiredAIFunction(
    AIFunctionFactory.Create((Delegate)IssueRefund, name: "issue_refund"));

// ── Argument validation as an auto-approval rule ──────────────────────
// AutoApprovalRules receive a ToolAutoApprovalRuleContext BEFORE execution —
// the pending call is context.FunctionCallContent (tool name + arguments) —
// and return true to auto-approve, false to require a human.
// Here: small positive refunds (more than $0, at most $50) are auto-approved;
// anything else escalates — including zero, negative and unreadable amounts.
// This is least-privilege + argument validation in one hook. (RefundRule, at
// the bottom of the file, holds the rule so it can be tested without a model.)
var approvalOptions = new ToolApprovalAgentOptions
{
    AutoApprovalRules =
    [
        (ToolAutoApprovalRuleContext context) =>
        {
            if (context.FunctionCallContent.Name == "issue_refund"
                && context.FunctionCallContent.Arguments is { } args
                && args.TryGetValue("amount", out object? raw))
            {
                bool auto = RefundRule.ShouldAutoApprove(raw, out string shown);
                Console.WriteLine(
                    $"   [rule] issue_refund {shown} -> {(auto ? "auto-approved" : "needs human approval")}");
                return new ValueTask<bool>(auto);
            }

            // delete_customer_record (and any unrecognised call): always escalate.
            return new ValueTask<bool>(false);
        }
    ]
};

var agentOptions = new ChatClientAgentOptions
{
    Name = "AdminAgent",
    ChatOptions = new ChatOptions
    {
        Instructions =
            "You are an admin assistant for a billing system. " +
            "To delete a customer, call delete_customer_record with the id. " +
            "To refund, call issue_refund with customerId and amount. " +
            "Just call the appropriate tool; do not ask the user to confirm.",
        Tools = [deleteTool, refundTool],
        // One tool call per turn keeps the approval gate clean: a single
        // approval-required call cannot drag siblings through with it.
        AllowMultipleToolCalls = false
    }
};

AIAgent agent = new ToolApprovalAgent(
    new ChatClientAgent(chatClient, agentOptions),
    approvalOptions);

// ── Scenario 1: a destructive delete — APPROVE the gate ───────────────
Console.WriteLine("-- Scenario 1: delete a record, then approve --");
AgentSession s1 = await agent.CreateSessionAsync(cts.Token);
await DriveWithDecision(agent, s1, "Delete customer C-1001 please.",
    approve: true, reason: "Verified ticket #4821; approved by on-call admin.", cts.Token);
Console.WriteLine();

// ── Scenario 2: an over-limit refund — REJECT the gate ────────────────
Console.WriteLine("-- Scenario 2: $5000 refund exceeds policy, reject --");
AgentSession s2 = await agent.CreateSessionAsync(cts.Token);
await DriveWithDecision(agent, s2, "Refund $5000 to customer C-1001.",
    approve: false, reason: "Refund of $5,000 is far above the $50 auto-approve limit; denied.", cts.Token);
Console.WriteLine();

// ── Scenario 3: a small refund — auto-approved by the rule ────────────
Console.WriteLine("-- Scenario 3: $20 refund is under the auto-approve limit --");
AgentSession s3 = await agent.CreateSessionAsync(cts.Token);
await DriveWithDecision(agent, s3, "Refund $20 to customer C-1001.",
    approve: false, reason: "(should not be asked — rule auto-approves)", cts.Token);
Console.WriteLine();

Console.WriteLine("=== Done ===");
return 0;

// ----------------------------------------------------------------------------
// Drives one prompt through the approval loop: run, inspect for approval
// requests, apply the human decision, resume on the SAME session.
// ----------------------------------------------------------------------------
static async Task DriveWithDecision(
    AIAgent agent, AgentSession session, string prompt,
    bool approve, string reason, CancellationToken ct)
{
    Console.WriteLine($">> {prompt}");
    AgentResponse response = await agent.RunAsync(prompt, session, cancellationToken: ct);

    List<ToolApprovalRequestContent> requests = response.Messages
        .SelectMany(m => m.Contents)
        .OfType<ToolApprovalRequestContent>()
        .ToList();

    if (requests.Count == 0)
    {
        // No human gate: either no tool was called, or a rule auto-approved it.
        Console.WriteLine($"<< (no approval needed) {response.Text}");
        return;
    }

    foreach (ToolApprovalRequestContent request in requests)
    {
        Console.WriteLine($"   [approval requested] callId={request.ToolCall.CallId} " +
                          $"decision={(approve ? "APPROVE" : "REJECT")}");
        var decision = new ChatMessage(
            ChatRole.User, [request.CreateResponse(approve, reason)]);
        response = await agent.RunAsync(decision, session, cancellationToken: ct);
    }

    Console.WriteLine($"<< {response.Text}");
}

// ── The refund rule ─────────────────────────────────────────────────
internal static class RefundRule
{
    public const decimal AutoApproveLimit = 50m;

    /// <summary>
    /// Auto-approve only a refund that is more than zero and at most the limit.
    /// The amount is read the same way on every machine (invariant culture);
    /// anything that is not a plain number — "1,5", "abc", null — escalates.
    /// </summary>
    public static bool ShouldAutoApprove(object? raw, out string shown)
    {
        if (!TryReadAmount(raw, out decimal amount))
        {
            shown = $"'{raw}' (unreadable amount)";
            return false;
        }
        shown = "$" + amount.ToString(CultureInfo.InvariantCulture);
        return amount > 0m && amount <= AutoApproveLimit;
    }

    static bool TryReadAmount(object? raw, out decimal amount)
    {
        const NumberStyles plain = NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign;
        switch (raw)
        {
            case JsonElement { ValueKind: JsonValueKind.Number } n:
                return n.TryGetDecimal(out amount);
            case JsonElement { ValueKind: JsonValueKind.String } t:
                return decimal.TryParse(t.GetString(), plain, CultureInfo.InvariantCulture, out amount);
            case string text:
                return decimal.TryParse(text, plain, CultureInfo.InvariantCulture, out amount);
            case decimal d:
                amount = d; return true;
            case int or long or double or float:
                amount = Convert.ToDecimal(raw, CultureInfo.InvariantCulture); return true;
            default:
                amount = 0m; return false;
        }
    }

    /// <summary>Offline check: dotnet run -- --self-test (runs under de-DE on purpose).</summary>
    public static int SelfTest()
    {
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");   // "1,5" would be 1.5 here
        (object? Raw, bool Auto)[] cases =
        [
            (0.01m, true), (50m, true), (20, true),
            (JsonDocument.Parse("20").RootElement, true),
            (JsonDocument.Parse("\"1.5\"").RootElement, true),
            ("1.5", true),
            (-10m, false), (0m, false), (50.01m, false), (5000, false),
            (JsonDocument.Parse("-10").RootElement, false),
            ("1,5", false), ("abc", false), ("", false), (null, false),
        ];
        int passed = 0;
        foreach ((object? raw, bool expected) in cases)
        {
            bool actual = ShouldAutoApprove(raw, out string shown);
            bool ok = actual == expected;
            passed += ok ? 1 : 0;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {shown,-28} -> {(actual ? "auto-approved" : "needs human approval")}");
        }
        Console.WriteLine($"RESULT {passed}/{cases.Length}");
        return passed == cases.Length ? 0 : 1;
    }
}
