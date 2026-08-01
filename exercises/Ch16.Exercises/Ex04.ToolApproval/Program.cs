// ============================================================================
// Exercise 04 — Tool-call Approval (Intermediate)
// ----------------------------------------------------------------------------
// Gate Excessive Agency (OWASP LLM06). An agent holds two tools:
//   - get_order_status : READ-ONLY  — safe to run unattended (auto-approved)
//   - cancel_order     : WRITE/destructive — must require a human decision
//
// Both are wrapped in ApprovalRequiredAIFunction. An AutoApprovalRule auto-
// approves the read-only call but escalates the write call. We then REJECT the
// cancel and prove its side effect (a counter) never fires.
//
// NOTE: ToolApprovalAgent / ToolApprovalAgentOptions carried [Experimental("MAAI001")]
// through MAF 1.13; they are GA in 1.14, so no <NoWarn> suppression is needed.
// ============================================================================

using System.ClientModel;
using System.ComponentModel;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
using IHost host = builder.Build();
IConfiguration config = host.Services.GetRequiredService<IConfiguration>();
IChatClient chatClient = CreateChatClient(config);

Console.WriteLine("=== Exercise: Tool-call Approval (LLM06) ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// Side-effect counter — proves a rejected cancel_order never executes.
int cancellations = 0;

[Description("Returns the current status of an order. Read-only and safe.")]
string GetOrderStatus([Description("the order id, e.g. O-77")] string orderId)
    => $"Order {orderId} is shipped and in transit.";

[Description("Cancels an order. Destructive: refunds and voids the shipment.")]
string CancelOrder([Description("the order id, e.g. O-77")] string orderId)
{
    cancellations++;                       // the side effect we must prevent on reject
    return $"Order {orderId} has been cancelled.";
}

AIFunction statusTool = new ApprovalRequiredAIFunction(
    AIFunctionFactory.Create((Delegate)GetOrderStatus, name: "get_order_status"));
AIFunction cancelTool = new ApprovalRequiredAIFunction(
    AIFunctionFactory.Create((Delegate)CancelOrder, name: "cancel_order"));

// Auto-approve the read-only call; everything else escalates to a human.
// (since 1.14: rules receive a ToolAutoApprovalRuleContext; the pending call is
// context.FunctionCallContent.)
var approvalOptions = new ToolApprovalAgentOptions
{
    AutoApprovalRules =
    [
        (ToolAutoApprovalRuleContext context) =>
            new ValueTask<bool>(context.FunctionCallContent.Name == "get_order_status")
    ]
};

var agentOptions = new ChatClientAgentOptions
{
    Name = "OrderAgent",
    ChatOptions = new ChatOptions
    {
        Instructions =
            "You manage orders. To check status call get_order_status. " +
            "To cancel call cancel_order. Just call the right tool; do not ask to confirm.",
        Tools = [statusTool, cancelTool],
        AllowMultipleToolCalls = false
    }
};

AIAgent agent = new ToolApprovalAgent(
    new ChatClientAgent(chatClient, agentOptions),
    approvalOptions);

// Scenario 1: read-only — auto-approved, runs unattended.
Console.WriteLine("-- Scenario 1: check status (auto-approved) --");
AgentSession s1 = await agent.CreateSessionAsync(cts.Token);
await Drive(agent, s1, "What is the status of order O-77?", approve: false, "n/a", cts.Token);
Console.WriteLine();

// Scenario 2: destructive cancel — REJECTED, must never execute.
Console.WriteLine("-- Scenario 2: cancel order, then REJECT --");
AgentSession s2 = await agent.CreateSessionAsync(cts.Token);
await Drive(agent, s2, "Cancel order O-77.", approve: false,
    "Customer has not confirmed; cancellation denied.", cts.Token);
Console.WriteLine();

Console.WriteLine(cancellations == 0
    ? "PROOF: cancel_order side effect fired 0 times — the rejected call never executed."
    : $"FAIL: cancel_order executed {cancellations} time(s) despite rejection.");
Console.WriteLine("\n=== Exercise complete! ===");

// Run the prompt, apply the decision to any approval requests, resume same session.
async Task Drive(AIAgent a, AgentSession session, string prompt,
    bool approve, string reason, CancellationToken ct)
{
    Console.WriteLine($">> {prompt}");
    AgentResponse response = await a.RunAsync(prompt, session, cancellationToken: ct);

    List<ToolApprovalRequestContent> requests = response.Messages
        .SelectMany(m => m.Contents)
        .OfType<ToolApprovalRequestContent>()
        .ToList();

    if (requests.Count == 0)
    {
        Console.WriteLine($"<< (no approval needed) {response.Text}");
        return;
    }

    foreach (ToolApprovalRequestContent request in requests)
    {
        Console.WriteLine($"   [approval requested] callId={request.ToolCall.CallId} " +
                          $"decision={(approve ? "APPROVE" : "REJECT")}");
        var decision = new ChatMessage(ChatRole.User, [request.CreateResponse(approve, reason)]);
        response = await a.RunAsync(decision, session, cancellationToken: ct);
    }

    Console.WriteLine($"<< {response.Text}");
}

static IChatClient CreateChatClient(IConfiguration configuration)
{
    string provider = configuration["LlmProvider:Provider"]?.ToLowerInvariant() ?? "ollama";
    return provider switch
    {
        "azure" => new AzureOpenAIClient(new Uri(configuration["LlmProvider:AzureOpenAI:Endpoint"] ?? throw new InvalidOperationException("Missing")), new DefaultAzureCredential())
            .GetChatClient(configuration["LlmProvider:AzureOpenAI:Model"] ?? "gpt-4o-mini").AsIChatClient(),
        "openai" => new OpenAI.OpenAIClient(configuration["LlmProvider:OpenAI:ApiKey"] ?? throw new InvalidOperationException("Missing"))
            .GetChatClient(configuration["LlmProvider:OpenAI:Model"] ?? "gpt-4o-mini").AsIChatClient(),
        "github" => new OpenAI.OpenAIClient(new ApiKeyCredential(configuration["LlmProvider:GitHub:Token"] ?? throw new InvalidOperationException("Missing")),
                new OpenAI.OpenAIClientOptions { Endpoint = new Uri(configuration["LlmProvider:GitHub:Endpoint"] ?? "https://models.github.ai/inference") })
            .GetChatClient(configuration["LlmProvider:GitHub:Model"] ?? "openai/gpt-4o-mini").AsIChatClient(),
        "ollama" => new OllamaSharp.OllamaApiClient(new Uri(configuration["LlmProvider:Ollama:Endpoint"] ?? "http://localhost:11434"), configuration["LlmProvider:Ollama:Model"] ?? "llama3.2"),
        _ => throw new InvalidOperationException($"Unknown provider: '{provider}'")
    };
}
