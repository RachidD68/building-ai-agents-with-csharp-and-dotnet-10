// ============================================================================
// Looping: LoopAgent and its evaluators
// ----------------------------------------------------------------------------
// LoopAgent re-invokes the whole agent until its evaluators are satisfied or
// LoopAgentOptions.MaxIterations runs out (LoopAgent.DefaultMaxIterations when
// you leave it null). Each LoopEvaluator returns LoopEvaluation.Stop() or
// Continue(feedback). Evaluators that ship in 1.28 include
// CompletionMarkerLoopEvaluator, TodoCompletionLoopEvaluator,
// BackgroundTaskCompletionLoopEvaluator, AIJudgeLoopEvaluator and
// DelegateLoopEvaluator. All of it is [Experimental("MAAI001")].
//
// Leaving the loop is not success. On the cap, LoopAgent returns the last
// draft exactly as it would after a Stop, and it does not evaluate the
// iteration that hit the cap. RecordingLoopEvaluator keeps each evaluator's
// last verdict, so this program can say which way the loop ended and exit
// non-zero when it ran out of iterations.
//
//   dotnet run                  live run; exit 0 only if the evaluators stopped it
//   dotnet run -- --self-test   offline checks with a scripted worker
// ============================================================================

using AgenticAI.Common.Extensions;
using Ch16.HarnessLoop;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

if (args.Contains("--self-test")) return await SelfTest.RunAsync();

HostApplicationBuilder hostBuilder = Host.CreateApplicationBuilder(args);
hostBuilder.Services.AddLlmChatClient(hostBuilder.Configuration,
    attemptTimeout: TimeSpan.FromMinutes(8));

using IHost host = hostBuilder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine("=== Looping with LoopAgent ===");
Console.WriteLine();

HarnessAgentOptions options = new()
{
    Name = "SummaryWriter",
    HarnessInstructions =
        "You write short product summaries. Facts you may use: the Contoso Ergonomic " +
        "Keyboard costs $79.99 and carries a two-year warranty. Never state a fact you " +
        "were not given. When you believe the summary is complete and meets every " +
        "requirement, end your reply with the exact word DONE.",
    ChatOptions = new ChatOptions { Instructions = "Write plainly. No marketing language." },
    DisableWebSearch = true,
    DisableFileMemory = true,
    DisableTodoProvider = true,
    DisableAgentModeProvider = true,
    DisableAgentSkillsProvider = true,
};

HarnessAgent worker = chatClient.AsHarnessAgent(options);

// The cheapest judge: a string check, no model call.
RecordingLoopEvaluator marker = new(new CompletionMarkerLoopEvaluator("DONE"));

// A rule the prompt never states, enforced in code where it can be tested.
RecordingLoopEvaluator requirements = new(new DelegateLoopEvaluator((ctx, ct) =>
{
    IReadOnlyList<string> problems = SummaryRule.Problems(ctx.LastResponse?.Text);
    Console.WriteLine($"   [evaluator] iteration {ctx.Iteration}: " +
                      (problems.Count == 0 ? "meets the rule" : string.Join("; ", problems)));
    return ValueTask.FromResult(problems.Count == 0
        ? LoopEvaluation.Stop()
        : LoopEvaluation.Continue($"Rewrite the summary: {string.Join("; ", problems)}. Keep it factual."));
}));

LoopAgentOptions loopOptions = new()
{
    MaxIterations = 3,                      // always bound an autonomous loop
    NonStreamingReturnsLastResponseOnly = true,
};

LoopAgent loop = new(worker, [marker, requirements], loopOptions);

Console.WriteLine($"LoopAgent wraps {worker.Name}; MaxIterations = {loopOptions.MaxIterations}");
Console.WriteLine($"Evaluators: CompletionMarkerLoopEvaluator(\"DONE\") + price and {SummaryRule.WordBudget}-word rule");
Console.WriteLine();

// Deliberately vague: the evaluator is what discovers the gap.
const string task = "Write a two-sentence summary of the Contoso Ergonomic Keyboard.";
Console.WriteLine($"You: {task}");
Console.WriteLine();

AgentSession session = await loop.CreateSessionAsync(cts.Token);
AgentResponse response = await loop.RunAsync(task, session, cancellationToken: cts.Token);

LoopOutcome outcome = RecordingLoopEvaluator.Outcome([marker, requirements]);
Console.WriteLine();
Console.WriteLine("Final answer:");
Console.WriteLine($"  {response.Text?.Trim()}");
Console.WriteLine();
Console.WriteLine(outcome == LoopOutcome.Stopped
    ? $"Outcome: STOPPED by the evaluators after iteration {requirements.LastIteration}."
    : $"Outcome: CAP REACHED ({loopOptions.MaxIterations} iterations); no evaluator accepted the last draft.");
Console.WriteLine();
Console.WriteLine("=== Harness loop demo complete! ===");
return RecordingLoopEvaluator.ExitCode(outcome);
