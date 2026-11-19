// ============================================================================
// Chapter 8 — Controlling the tool-calling loop (FunctionInvokingChatClient)
// ----------------------------------------------------------------------------
// The engine that runs the tool-calling loop is FunctionInvokingChatClient — a
// DelegatingChatClient that receives FunctionCallContent from the model,
// invokes the matching AIFunction, returns FunctionResultContent, and repeats.
// Two guardrails keep a misbehaving loop from running forever or burning your
// whole token budget on a tool that keeps failing:
//
//   • MaximumIterationsPerRequest        (default 40) — caps the number of
//     model<->tool round-trips per request. When the cap is hit the loop stops
//     and returns whatever the model has produced so far.
//   • MaximumConsecutiveErrorsPerRequest (default 3)  — after this many
//     CONSECUTIVE failing invocations the exception is rethrown to the caller.
//     Set it to 0 to terminate on the first error.
//   • IncludeDetailedErrors (default false) — when true, the failing tool's
//     exception message is fed back to the model so it can adapt; a data-leak
//     risk in production, so it is off by default.
//
// These properties live on FunctionInvokingChatClient, not on ChatClientAgent.
// To set them we build the pipeline ourselves with UseFunctionInvocation(...)
// and tell the agent to use our client AS IS (UseProvidedChatClientAsIs = true)
// so it does not add a second, default-configured invocation layer on top.
//
// Both demos force a specific tool with ChatToolMode.RequireSpecific so the
// loop is deterministic enough for a stable transcript.
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
IChatClient innerClient = host.Services.GetRequiredService<IChatClient>();

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine("=== Chapter 8: Loop Control (FunctionInvokingChatClient) ===\n");

// ── Demo 1: cap the round-trips with MaximumIterationsPerRequest ────
// The tool never satisfies the model — it always reports "still processing,
// call again" — so without a cap the loop would run forever. We cap it at 3.
int pollCount = 0;
AIFunction pollTool = AIFunctionFactory.Create(
    [Description("Polls a long-running export job for its status.")]
    ([Description("The export job id, e.g. 'JOB-77'.")] string jobId) =>
    {
        pollCount++;
        return "Status: STILL PROCESSING. Call poll_job again to continue.";
    },
    name: "poll_job");

Console.WriteLine("--- Demo 1: MaximumIterationsPerRequest = 3 (runaway poll loop) ---");
pollCount = 0;
await RunCappedAsync(
    pollTool,
    "poll_job",
    "Has export job JOB-77 finished yet?",
    maxIterations: 3,
    maxErrors: 3,
    includeDetail: false);
Console.WriteLine(pollCount >= 3
    ? $"poll_job was invoked {pollCount} time(s) before the cap stopped the loop."
    : $"poll_job was invoked {pollCount} time(s); the model stopped polling on its own before the cap.");

// ── Demo 2: terminate on first error with MaximumConsecutiveErrorsPerRequest = 0
Console.WriteLine("\n--- Demo 2: MaximumConsecutiveErrorsPerRequest = 0 (failing tool) ---");
AIFunction flakyTool = AIFunctionFactory.Create(QueryLedger, name: "query_ledger");
await RunCappedAsync(
    flakyTool,
    "query_ledger",
    "What was the closing balance for account 4001?",
    maxIterations: 5,
    maxErrors: 0,
    includeDetail: true);

Console.WriteLine("\n=== Loop-control demo complete! ===");

// ----------------------------------------------------------------------------
// Builds an agent whose FunctionInvokingChatClient is configured with the
// supplied limits, forces the named tool, and reports how the loop ended.
// ----------------------------------------------------------------------------
async Task RunCappedAsync(
    AIFunction tool,
    string toolName,
    string prompt,
    int maxIterations,
    int maxErrors,
    bool includeDetail)
{
    // Decorate the raw client with a CONFIGURED function-invocation layer.
    IChatClient pipeline = innerClient
        .AsBuilder()
        .UseFunctionInvocation(configure: c =>
        {
            c.MaximumIterationsPerRequest = maxIterations;
            c.MaximumConsecutiveErrorsPerRequest = maxErrors;
            c.IncludeDetailedErrors = includeDetail;
        })
        .Build();

    // UseProvidedChatClientAsIs = true → the agent will NOT add its own default
    // function-invocation decorator on top of ours, so our limits take effect.
    ChatClientAgent agent = new(
        pipeline,
        new ChatClientAgentOptions
        {
            Name = "LoopAssistant",
            UseProvidedChatClientAsIs = true,
            ChatOptions = new ChatOptions
            {
                Instructions = "You are an assistant. Use the provided tool to answer.",
                ToolMode = ChatToolMode.RequireSpecific(toolName),
                Tools = [tool],
            },
        });

    AgentSession session = await agent.CreateSessionAsync(cts.Token);

    try
    {
        AgentResponse response = await agent.RunAsync(
            prompt, session, cancellationToken: cts.Token);

        // Reached when the ITERATION cap stops the loop: no exception is thrown,
        // the run just returns with whatever the model last produced.
        string text = string.IsNullOrWhiteSpace(response.Text)
            ? "(no final text — loop hit the iteration cap before the model concluded)"
            : response.Text;
        Console.WriteLine($"Run completed without throwing. Agent: {text}");
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        // Reached when the ERROR cap rethrows the tool's exception to us.
        Console.WriteLine($"Loop terminated by error guardrail: {ex.GetType().Name}: {ex.Message}");
    }
}

// ----------------------------------------------------------------------------
// A tool that always fails. Throwing (rather than returning an error string) is
// what drives the MaximumConsecutiveErrorsPerRequest guardrail — see the
// chapter's "graceful degradation" section for when to return vs. throw.
// ----------------------------------------------------------------------------
[Description("Looks up the closing balance for a ledger account.")]
static string QueryLedger(
    [Description("The ledger account number, e.g. '4001'.")] string account)
    => throw new InvalidOperationException(
        $"LEDGER-DB-503: ledger database is unavailable (account '{account}').");
