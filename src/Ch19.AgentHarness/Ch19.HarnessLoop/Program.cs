// ============================================================================
// Chapter 19 — Looping: LoopAgent and its evaluators
// ----------------------------------------------------------------------------
// Chapter 13 hand-rolled a self-correcting loop: run, judge, feed the critique
// back, run again, stop on a marker or a cap. The harness ships that shape as
// LoopAgent, and the judgement as a LoopEvaluator.
//
// LoopAgent is the OUTERMOST decorator: it re-invokes the whole agent, so
// every iteration gets the harness's tools, approvals and telemetry.
//
// The evaluators that ship at 1.20:
//   CompletionMarkerLoopEvaluator        stop when the text contains a marker
//   TodoCompletionLoopEvaluator          stop when the to-do list is empty
//   BackgroundTaskCompletionLoopEvaluator stop when delegated work finishes
//   AIJudgeLoopEvaluator                 stop when a judge model says criteria are met
//   DelegateLoopEvaluator                stop when YOUR lambda says so
//
// Every one returns a LoopEvaluation: Continue(feedback) to go round again
// with a note for the model, or Stop() to finish. All of this is
// [Experimental("MAAI001")] at 1.20.
//
// ALWAYS bound an autonomous loop. LoopAgentOptions.MaxIterations defaults to
// 10; this demo sets 3 so a local model cannot spin.
// ============================================================================

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgenticAI.Common.Extensions;

HostApplicationBuilder hostBuilder = Host.CreateApplicationBuilder(args);
hostBuilder.Services.AddLlmChatClient(hostBuilder.Configuration,
    attemptTimeout: TimeSpan.FromMinutes(8));

using IHost host = hostBuilder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine("=== Chapter 19: Looping with LoopAgent ===");
Console.WriteLine();

// ── The agent that does the work ──────────────────────────────────────
HarnessAgentOptions options = new()
{
    Name = "SummaryWriter",
    HarnessInstructions =
        "You write short product summaries. When you believe the summary is complete " +
        "and meets every requirement, end your reply with the exact word DONE.",
    ChatOptions = new ChatOptions { Instructions = "Write plainly. No marketing language." },
    DisableWebSearch = true,
    DisableFileMemory = true,
    DisableTodoProvider = true,
    DisableAgentModeProvider = true,
    DisableAgentSkillsProvider = true,
};

HarnessAgent worker = chatClient.AsHarnessAgent(options);

// ── Evaluator 1: a completion marker ──────────────────────────────────
// The cheapest possible judge -- no extra model call, just a string check.
CompletionMarkerLoopEvaluator markerEvaluator = new("DONE");

// ── Evaluator 2: your own rule ────────────────────────────────────────
// DelegateLoopEvaluator lets you enforce something a marker cannot: here,
// that the summary actually mentions the price and the warranty.
int lastIteration = 0;
DelegateLoopEvaluator requirementsEvaluator = new((ctx, ct) =>
{
    // LoopContext gives you the run so far: which pass this is, and what the
    // agent last said. That is all a judge needs.
    lastIteration = ctx.Iteration;
    string text = ctx.LastResponse?.Text ?? string.Empty;
    bool hasPrice = text.Contains('$');
    bool hasWarranty = text.Contains("warranty", StringComparison.OrdinalIgnoreCase);

    Console.WriteLine($"   [evaluator] iteration {ctx.Iteration}: price={hasPrice}, warranty={hasWarranty}");

    if (hasPrice && hasWarranty)
    {
        return ValueTask.FromResult(LoopEvaluation.Stop());
    }

    List<string> missing = [];
    if (!hasPrice) missing.Add("the price");
    if (!hasWarranty) missing.Add("the warranty term");
    return ValueTask.FromResult(
        LoopEvaluation.Continue($"The summary is missing {string.Join(" and ", missing)}. Add it."));
});

// ── The loop ──────────────────────────────────────────────────────────
// Two evaluators: the loop continues while either wants another pass.
LoopAgentOptions loopOptions = new()
{
    MaxIterations = 3,                      // ALWAYS bound an autonomous loop
    NonStreamingReturnsLastResponseOnly = true,
};

LoopAgent loop = new(worker, [markerEvaluator, requirementsEvaluator], loopOptions);

Console.WriteLine($"LoopAgent wraps {worker.Name}; MaxIterations = {loopOptions.MaxIterations}");
Console.WriteLine("Evaluators: CompletionMarkerLoopEvaluator(\"DONE\") + DelegateLoopEvaluator(price & warranty)");
Console.WriteLine();

const string task =
    "Write a two-sentence summary of the Contoso Ergonomic Keyboard. " +
    "It costs $79.99 and carries a two-year warranty.";

Console.WriteLine($"You: {task}");
Console.WriteLine();

AgentSession session = await loop.CreateSessionAsync(cts.Token);
AgentResponse response = await loop.RunAsync(task, session, cancellationToken: cts.Token);

Console.WriteLine();
Console.WriteLine("Final answer:");
Console.WriteLine($"  {response.Text?.Trim()}");
Console.WriteLine();
Console.WriteLine($"Last iteration the evaluator saw: {lastIteration} (cap was {loopOptions.MaxIterations})");
Console.WriteLine();
Console.WriteLine("=== Harness loop demo complete! ===");
