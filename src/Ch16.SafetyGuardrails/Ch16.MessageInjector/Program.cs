// ============================================================================
// Chapter 16 — Injecting safety context mid-run with AIContextProvider
// ----------------------------------------------------------------------------
// Middleware (DelegatingChatClient) wraps the raw IChatClient call. It is
// position-agnostic: it sees one request and one response and does not know it
// is the third model turn of a tool-calling run. To add a message into the
// agent's INVOCATION LIFECYCLE — before each model invocation in a multi-step
// turn — the real hook is AIContextProvider, the same abstraction Chapter 6
// used for memory, here turned to safety.
//
//   DelegatingChatClient (middleware)
//     • Wraps GetResponseAsync / GetStreamingResponseAsync.
//     • Whole-request scope, position-agnostic.
//
//   AIContextProvider
//     • ProvideAIContextAsync runs BEFORE every model invocation in the run.
//     • StoreAIContextAsync runs AFTER each one (read-only auditing).
//     • Returns a structured AIContext (Instructions, Messages, Tools) that is
//       merged into that invocation.
//     • Opt in per agent via ChatClientAgentOptions.EnableMessageInjection.
//
// This demo registers an AuditReminderProvider that (a) supplies the agent's
// standing instructions and (b) injects an [audit] system message on every
// second model invocation. Compose it with the Ch15 middleware
// (LoggingMiddleware, TimingMiddleware, ContentFilterMiddleware): the provider
// handles per-invocation concerns, the middleware handles whole-request ones.
// ============================================================================

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgenticAI.Common.Extensions;

HostApplicationBuilder hostBuilder = Host.CreateApplicationBuilder(args);
hostBuilder.Services.AddLlmChatClient(hostBuilder.Configuration);

using IHost host = hostBuilder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();

Console.WriteLine("=== Chapter 15: AIContextProvider mid-run injection demo ===");
Console.WriteLine();

// ── A toy tool to make the function-call loop interesting ─────────────
[System.ComponentModel.Description("Look up the current shipping status of an order by id.")]
static string GetOrderStatus(string orderId) =>
    orderId switch
    {
        "A100" => "shipped 2 days ago",
        "A101" => "processing",
        _      => "unknown order"
    };

AIFunction orderTool = AIFunctionFactory.Create((Delegate)GetOrderStatus, name: "get_order_status");

// ── The provider: standing instructions + an audit reminder every 2nd call ──
AuditReminderProvider reminder = new(
    everyN: 2,
    reminder: "[audit] Conversation history is logged for compliance review.");

// ── Wire the agent via ChatClientAgentOptions ─────────────────────────
// EnableMessageInjection opts this agent into mid-run message injection;
// AIContextProviders registers the provider; ChatOptions.Tools carries the
// tool (ChatClientAgentOptions has no Tools/Instructions of its own — the
// provider supplies the instructions, the same way Chapter 6 does).
// EnableMessageInjection opts this agent into mid-run message injection, and it
// is still flagged [Experimental("MAAI001")] as of MAF 1.13 — the API exists and
// works; the diagnostic just signals it may change. Suppress it deliberately.
#pragma warning disable MAAI001
ChatClientAgentOptions options = new()
{
    Name = "SupportAgent",
    Description = "Customer-support agent with an audit-reminder context provider.",
    EnableMessageInjection = true,
    AIContextProviders = [reminder],
    ChatOptions = new ChatOptions { Tools = [orderTool] }
};
#pragma warning restore MAAI001

ChatClientAgent agent = new(chatClient, options: options);

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

string[] prompts =
[
    "What is the status of order A100?",
    "And order A101?",
    "And A999?",
    "Thanks — can you summarise all three?"
];

AgentSession session = await agent.CreateSessionAsync(cts.Token);

foreach (string prompt in prompts)
{
    Console.WriteLine($">> {prompt}");
    AgentResponse response = await agent.RunAsync(prompt, session, cancellationToken: cts.Token);
    Console.WriteLine($"<< {response.Text}");
    Console.WriteLine();
}

Console.WriteLine("=== Done ===");

// ============================================================================
// The context provider
// ============================================================================

/// <summary>
/// An <see cref="AIContextProvider"/> that supplies the agent's standing
/// instructions on every model invocation and injects an audit reminder as a
/// system message on every Nth invocation. Compare with the memory-focused
/// provider in Chapter 6 — same hook, different job.
/// </summary>
public sealed class AuditReminderProvider(int everyN, string reminder)
    : AIContextProvider
{
    private int _invocations;

    /// <summary>Runs BEFORE every model invocation in the run.</summary>
    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken)
    {
        _invocations++;

        AIContext aiContext = new()
        {
            Instructions = "You are a customer-support agent. Use tools to answer."
        };

        if (_invocations % everyN == 0)
        {
            Console.WriteLine($"   [context-provider] injecting: {reminder}");
            aiContext.Messages = [new ChatMessage(ChatRole.System, reminder)];
        }

        return new ValueTask<AIContext>(aiContext);
    }

    /// <summary>Runs AFTER each model invocation — read-only auditing.</summary>
    protected override ValueTask StoreAIContextAsync(
        InvokedContext context,
        CancellationToken cancellationToken)
    {
        int responseMessages = context.ResponseMessages?.Count() ?? 0;
        Console.WriteLine($"   [context-provider] invocation #{_invocations} " +
                          $"complete ({responseMessages} response message(s)).");
        return ValueTask.CompletedTask;
    }
}
