// ============================================================================
// Exercise 01: tool-call approval with the policy first (Intermediate)
// ----------------------------------------------------------------------------
// An order agent holds two tools, both wrapped in ApprovalRequiredAIFunction:
//   - get_order_status : read-only, auto-approved by a ToolApprovalAgent rule
//   - cancel_order     : cancels the order and refunds the amount; gated
//
// Before a reviewer sees a cancel request, ActionPolicy evaluates it (the
// order's paid total stands in for the account balance). Deny is rejected
// without asking anyone; RequireApproval goes to the reviewer, whose decision
// is stored server-side, bound to the exact arguments; Allow goes through.
//
// Acceptance:
//   - a rejected cancel never fires (side-effect counter = 0);
//   - the status lookup runs unattended;
//   - an approval reused with changed arguments is refused.
//
//   dotnet run                    the scenarios against the configured model
//   dotnet run -- --self-test     offline, with a scripted model; RESULT n/n
// ============================================================================

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ch08.ActionPolicy;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgenticAI.Common.Extensions;

bool selfTest = args.Contains("--self-test");
IChatClient chatClient;
if (selfTest)
{
    chatClient = new ScriptedChatClient();
}
else
{
    HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddLlmChatClient(builder.Configuration);
    IHost host = builder.Build();
    chatClient = host.Services.GetRequiredService<IChatClient>();
}

Console.WriteLine(selfTest ? "=== Exercise: approval, policy first (self-test) ===\n"
                           : "=== Exercise: approval, policy first ===\n");

var orders = new OrderDesk();
AIAgent agent = orders.CreateAgent(chatClient);

int reviews = 0;
bool reviewerApproves = false;
ReviewerDecision Review(FunctionCallContent call)
{
    reviews++;
    return new("rita", Roles.FinanceReviewer, reviewerApproves);
}

int passed = 0, total = 0;
void Expect(string name, bool ok)
{
    total++; passed += ok ? 1 : 0;
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
}

string Prompt(string scripted, string natural) => selfTest ? scripted : natural;

Console.WriteLine("-- 1. status lookup (read-only) --");
List<string> log1 = await orders.RunAsync(agent, Prompt("status O-77", "What is the status of order O-77?"), Review);
Expect("get_order_status runs unattended", orders.StatusLookups == 1 && log1.Count == 0);

Console.WriteLine("-- 2. cancel O-77 for 120 USD, reviewer REJECTS --");
_ = await orders.RunAsync(agent, Prompt("cancel O-77 120 USD", "Cancel order O-77 and refund 120 USD."), Review);
Expect("a rejected cancel never fires (side-effect counter = 0)", orders.Cancellations == 0 && reviews == 1);

Console.WriteLine("-- 3. cancel O-88 for 120 USD, reviewer approves --");
reviewerApproves = true;
_ = await orders.RunAsync(agent, Prompt("cancel O-88 120 USD", "Cancel order O-88 and refund 120 USD."), Review);
Expect("an approved cancel fires once", orders.Cancellations == 1 && reviews == 2);

Console.WriteLine("-- 4. the O-88 approval reused for 110 USD --");
List<string> log4 = await orders.RunAsync(agent, Prompt("cancel O-88 110 USD", "Cancel order O-88 and refund 110 USD."), Review);
Expect("an approval reused with changed arguments is refused, without asking the reviewer",
    orders.Cancellations == 1 && reviews == 2 && log4.Any(l => l.Contains("approval_invalid")));

Console.WriteLine($"\nCancellations: {orders.Cancellations}, reviewer asked {reviews} time(s).");
if (!selfTest) return 0;
Console.WriteLine($"RESULT {passed}/{total}");
return passed == total ? 0 : 1;

internal sealed record Order(string OrderId, string CustomerId, decimal PaidTotal, string Currency, long Version);

internal sealed record ReviewerDecision(string ReviewerId, string ReviewerRole, bool Approved);

/// <summary>The order system, its tools and the policy gate in front of cancel_order.</summary>
internal sealed class OrderDesk
{
    const string Tenant = "contoso";
    readonly Caller _admin = new(Tenant, "ann", new HashSet<string> { Permissions.SubmitReimbursement });
    readonly Dictionary<string, Order> _orders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["O-77"] = new("O-77", "C-77", 120.00m, "USD", 2),
        ["O-88"] = new("O-88", "C-88", 120.00m, "USD", 4),
    };
    readonly ConcurrentDictionary<Guid, ApprovalRecord> _approvals = new();   // server-side store
    readonly HashSet<string> _cancelled = new(StringComparer.OrdinalIgnoreCase);
    int _cancellations, _lookups;

    public int Cancellations => Volatile.Read(ref _cancellations);
    public int StatusLookups => Volatile.Read(ref _lookups);

    public AIAgent CreateAgent(IChatClient chatClient)
    {
        [Description("Returns the current status of an order. Read-only and safe.")]
        string GetOrderStatus([Description("the order id, e.g. O-77")] string orderId)
        {
            Interlocked.Increment(ref _lookups);
            return $"Order {orderId} is shipped and in transit.";
        }

        [Description("Cancels an order and refunds the given amount. Destructive: needs approval.")]
        string CancelOrder(
            [Description("the order id, e.g. O-77")] string orderId,
            [Description("the amount to refund, e.g. 120.00")] decimal refundAmount,
            [Description("ISO currency code, e.g. USD")] string currency)
        {
            // Checked again at execution: the framework's approval is not the business approval.
            PolicyResult policy = Check(Call(orderId, refundAmount, currency));
            if (policy.Decision != Decision.Allow) return $"Cancel refused ({policy.Reason}).";
            lock (_cancelled)
            {
                if (!_cancelled.Add(orderId.Trim())) return $"Order {orderId} was already cancelled.";
                _cancellations++;                     // the side effect a rejection must prevent
            }
            return $"Order {orderId} has been cancelled; {refundAmount} {currency} refunded.";
        }

        AIFunction status = new ApprovalRequiredAIFunction(
            AIFunctionFactory.Create((Delegate)GetOrderStatus, name: "get_order_status"));
        AIFunction cancel = new ApprovalRequiredAIFunction(
            AIFunctionFactory.Create((Delegate)CancelOrder, name: "cancel_order"));

        ChatClientAgent inner = new(chatClient, new ChatClientAgentOptions
        {
            Name = "OrderAgent",
            ChatOptions = new ChatOptions
            {
                Instructions = "You manage orders. To check status call get_order_status. To cancel, call " +
                               "cancel_order with the order id, the refund amount and the currency. " +
                               "Just call the right tool; do not ask to confirm.",
                Tools = [status, cancel],
                AllowMultipleToolCalls = false,
            },
        });

        // Auto-approve the read-only call only; every cancel goes through the loop.
        return new ToolApprovalAgent(inner, new ToolApprovalAgentOptions
        {
            AutoApprovalRules =
            [
                (ToolAutoApprovalRuleContext context) =>
                    ValueTask.FromResult(context.FunctionCallContent.Name == "get_order_status"),
            ],
        });
    }

    /// <summary>Run, check each request with ActionPolicy, ask the reviewer only on RequireApproval, resume.</summary>
    public async Task<List<string>> RunAsync(AIAgent agent, string prompt,
        Func<FunctionCallContent, ReviewerDecision> reviewer)
    {
        List<string> log = [];
        Console.WriteLine($">> {prompt}");
        AgentSession session = await agent.CreateSessionAsync();
        AgentResponse response = await agent.RunAsync(prompt, session);
        for (int round = 1; response.Messages.SelectMany(m => m.Contents).OfType<ToolApprovalRequestContent>().ToList()
             is { Count: > 0 } requests && round <= 5; round++)
        {
            List<AIContent> decisions = [];
            foreach (ToolApprovalRequestContent request in requests)
            {
                var call = (FunctionCallContent)request.ToolCall;
                PolicyResult policy = Check(call);
                bool approved = policy.Decision switch
                {
                    Decision.Allow => true,
                    Decision.RequireApproval => RecordReview(call, reviewer),
                    _ => false,
                };
                string line = $"[approval] {call.Name} policy={policy.Decision}/{policy.Reason} -> " +
                              (approved ? "APPROVED" : "REJECTED");
                log.Add(line);
                Console.WriteLine($"   {line}");
                decisions.Add(request.CreateResponse(approved, approved ? policy.Reason : $"denied: {policy.Reason}"));
            }
            response = await agent.RunAsync(new ChatMessage(ChatRole.User, decisions), session);
        }
        Console.WriteLine($"<< {response.Text}");
        return log;
    }

    PolicyResult Check(FunctionCallContent call)
    {
        if (call.Name != "cancel_order") return new(Decision.Deny, "not_a_cancel");
        if (!TryProposal(call, out EmployeeAccount? account, out ReimbursementProposal? proposal))
            return new(Decision.Deny, "invalid_arguments");
        _approvals.TryGetValue(proposal.OperationId, out ApprovalRecord? approval);
        return ReimbursementPolicy.Evaluate(_admin, account, proposal, approval, DateTimeOffset.UtcNow);
    }

    bool RecordReview(FunctionCallContent call, Func<FunctionCallContent, ReviewerDecision> reviewer)
    {
        if (!TryProposal(call, out _, out ReimbursementProposal? proposal)) return false;
        ReviewerDecision d = reviewer(call);
        _approvals[proposal.OperationId] = new ApprovalRecord(
            ReimbursementPolicy.Digest(_admin.TenantId, _admin.SubjectId, proposal),
            _admin.TenantId, _admin.SubjectId, d.ReviewerId, d.ReviewerRole,
            ReimbursementPolicy.Version, DateTimeOffset.UtcNow.AddMinutes(15), d.Approved);
        return Check(call).Decision == Decision.Allow;
    }

    bool TryProposal(FunctionCallContent call,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out EmployeeAccount? account,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ReimbursementProposal? proposal)
    {
        account = null; proposal = null;
        IDictionary<string, object?> a = call.Arguments ?? new Dictionary<string, object?>();
        if (Text(Arg(a, "orderId")) is not { } orderId || !_orders.TryGetValue(orderId.Trim(), out Order? order) ||
            !TryAmount(Arg(a, "refundAmount"), out decimal amount) ||
            Text(Arg(a, "currency")) is not { } currency)
            return false;
        // One business operation per order cancel, stable across retries.
        var operationId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"{Tenant}/cancel/{order.OrderId}")).AsSpan(0, 16));
        account = new EmployeeAccount(Tenant, order.CustomerId, order.Currency, order.PaidTotal, order.Version);
        proposal = new ReimbursementProposal(operationId, order.CustomerId, amount, currency, order.Version);
        return true;
    }

    static FunctionCallContent Call(string orderId, decimal amount, string currency) =>
        new("tool-body", "cancel_order", new Dictionary<string, object?>
        {
            ["orderId"] = orderId, ["refundAmount"] = amount, ["currency"] = currency,
        });

    static object? Arg(IDictionary<string, object?> args, string name) =>
        args.TryGetValue(name, out object? value) ? value : null;

    static string? Text(object? raw) => raw switch
    {
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
        _ => null,
    };

    static bool TryAmount(object? raw, out decimal amount)
    {
        switch (raw)
        {
            case decimal d: amount = d; return true;
            case JsonElement { ValueKind: JsonValueKind.Number } n: return n.TryGetDecimal(out amount);
            case JsonElement { ValueKind: JsonValueKind.String } s:
                return decimal.TryParse(s.GetString(), System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out amount);
            default: amount = 0m; return false;
        }
    }
}

/// <summary>
/// The scripted model for --self-test: "status O-77" or "cancel O-88 120 USD"
/// becomes one tool call with JSON arguments; after a tool result it answers in text.
/// </summary>
internal sealed class ScriptedChatClient : IChatClient
{
    int _calls;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        List<ChatMessage> list = [.. messages];
        if (list[^1].Contents.OfType<FunctionResultContent>().LastOrDefault() is { } result)
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, $"Tool result: {result.Result}")));

        string[] w = (list.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string id = $"call-{Interlocked.Increment(ref _calls)}";
        FunctionCallContent? call = w switch
        {
            ["status", var o] => new(id, "get_order_status", Args($$"""{"orderId":"{{o}}"}""")),
            ["cancel", var o, var amount, var cur] => new(id, "cancel_order",
                Args($$"""{"orderId":"{{o}}","refundAmount":{{amount}},"currency":"{{cur}}"}""")),
            _ => null,
        };
        return Task.FromResult(new ChatResponse(call is null
            ? new ChatMessage(ChatRole.Assistant, "No tool needed.")
            : new ChatMessage(ChatRole.Assistant, [call])));
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

    static Dictionary<string, object?> Args(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!
            .ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
}
