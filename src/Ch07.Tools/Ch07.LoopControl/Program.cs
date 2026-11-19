// ============================================================================
// Controlling the tool-calling loop (FunctionInvokingChatClient)
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
//
// A real model may stop polling on its own before any cap is reached, so the
// live run proves little about the brakes. `dotnet run -- --self-test` drives
// the SAME agent factory with a scripted model that never stops calling the
// tool, and asserts what each brake does. No network, no model.
// ============================================================================

using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgenticAI.Common.Extensions;

if (args.Contains("--self-test")) return await LoopSelfTest.RunAsync();

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);

using IHost host = builder.Build();
IChatClient innerClient = host.Services.GetRequiredService<IChatClient>();

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine("=== Loop Control (FunctionInvokingChatClient) ===\n");

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
return 0;

// ----------------------------------------------------------------------------
// Builds an agent with the supplied limits, forces the named tool, and reports
// how the loop ended.
// ----------------------------------------------------------------------------
async Task RunCappedAsync(
    AIFunction tool,
    string toolName,
    string prompt,
    int maxIterations,
    int maxErrors,
    bool includeDetail)
{
    ChatClientAgent agent = LoopAgent.Create(innerClient, tool, toolName, maxIterations, maxErrors, includeDetail);

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

internal static class LoopAgent
{
    /// <summary>
    /// An agent whose FunctionInvokingChatClient carries the supplied limits.
    /// The live demo and the self-test both build their agent here.
    /// </summary>
    public static ChatClientAgent Create(IChatClient innerClient, AIFunction tool, string toolName,
        int maxIterations, int maxErrors, bool includeDetail)
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
        return new ChatClientAgent(
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
    }
}

// ----------------------------------------------------------------------------
// Offline proof of the brakes: dotnet run -- --self-test
// ----------------------------------------------------------------------------
internal static class LoopSelfTest
{
    static int s_passed, s_total;

    public static async Task<int> RunAsync()
    {
        Console.WriteLine("=== Loop Control: self-test (scripted model, no network) ===\n");

        // 1. The iteration cap. The scripted model asks for poll_job on every turn, forever.
        int polls = 0;
        AIFunction poll = AIFunctionFactory.Create(
            (string jobId) => { polls++; return "Status: STILL PROCESSING. Call poll_job again to continue."; },
            name: "poll_job");
        var looping = new ScriptedLoopModel("poll_job", new() { ["jobId"] = "JOB-77" });
        AgentResponse capped = await LoopAgent.Create(looping, poll, "poll_job", 3, 3, false)
            .RunAsync("Has export job JOB-77 finished yet?");
        Console.WriteLine($"-- MaximumIterationsPerRequest = 3, a model that never stops calling poll_job --");
        Console.WriteLine($"   poll_job ran {polls} time(s); the model was called {looping.Calls} time(s)");
        Console.WriteLine($"   the last request carried {looping.LastRequestTools}");
        Console.WriteLine($"   the run returned: {capped.Text}");
        Expect("the tool ran exactly 3 times, then the loop stopped invoking it", polls == 3);
        Expect("the last request went out with no tools, so the model had to answer in text",
            looping.Calls == 4 && looping.LastRequestTools.StartsWith("0 tool(s)"));
        Expect("the run returned that answer normally, with no exception",
            capped.Text.StartsWith("I stopped checking"));

        // 2. The error brake at 0: the first failure is rethrown to the caller.
        var failing = new ScriptedLoopModel("query_ledger", new() { ["account"] = "4001" });
        AIFunction ledger = AIFunctionFactory.Create(Fail, name: "query_ledger");
        string? thrown = await ThrowsAsync(LoopAgent.Create(failing, ledger, "query_ledger", 5, 0, false));
        Console.WriteLine("\n-- MaximumConsecutiveErrorsPerRequest = 0, a tool that always throws --");
        Console.WriteLine($"   {thrown ?? "no exception"}");
        Expect("the first failure is rethrown as the tool's own exception",
            thrown is not null && thrown.StartsWith("InvalidOperationException: LEDGER-DB-503") && failing.Calls == 1);

        // 3. The error brake at 3, detailed errors off: what the model is told.
        var observing = new ScriptedLoopModel("query_ledger", new() { ["account"] = "4001" });
        string? thrownAfterRetries = await ThrowsAsync(LoopAgent.Create(observing, ledger, "query_ledger", 10, 3, false));
        Console.WriteLine("\n-- MaximumConsecutiveErrorsPerRequest = 3, IncludeDetailedErrors = false --");
        Console.WriteLine($"   the model was called {observing.Calls} time(s), then: {thrownAfterRetries ?? "no exception"}");
        Console.WriteLine($"   the model saw: {observing.ResultsSeen.FirstOrDefault() ?? "(nothing)"}");
        Expect("three failures go back to the model, the fourth is rethrown",
            thrownAfterRetries is not null && observing.Calls == 4);
        Expect("with detailed errors off, the model never sees the exception message",
            observing.ResultsSeen.Count == 3 && observing.ResultsSeen.All(r => !r.Contains("LEDGER-DB-503")));

        Console.WriteLine($"\nRESULT {s_passed}/{s_total}");
        return s_passed == s_total ? 0 : 1;
    }

    static string Fail(string account) =>
        throw new InvalidOperationException($"LEDGER-DB-503: ledger database is unavailable (account '{account}').");

    static async Task<string?> ThrowsAsync(ChatClientAgent agent)
    {
        try
        {
            await agent.RunAsync("What was the closing balance for account 4001?");
            return null;
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    static void Expect(string name, bool ok)
    {
        s_total++;
        if (ok) s_passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    }
}

/// <summary>
/// A model that answers every request with a call to the same tool, and
/// records the tool results it is sent back.
/// </summary>
internal sealed class ScriptedLoopModel(string toolName, Dictionary<string, object?> arguments) : IChatClient
{
    public int Calls { get; private set; }
    public List<string> ResultsSeen { get; } = [];
    public string LastRequestTools { get; private set; } = "";

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Calls++;
        LastRequestTools = $"{options?.Tools?.Count ?? 0} tool(s), tool mode {Describe(options?.ToolMode)}";
        ResultsSeen.Clear();
        ResultsSeen.AddRange(messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>()
            .Select(r => r.Result?.ToString() ?? ""));
        if (options?.Tools is not { Count: > 0 })      // no tool left to call: answer in text
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                "I stopped checking: the job was still processing at the last poll.")));
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent($"call-{Calls}", toolName, arguments)])));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (ChatResponseUpdate update in response.ToChatResponseUpdates()) yield return update;
    }

    static string Describe(ChatToolMode? mode) => mode switch
    {
        null => "(not set)",
        RequiredChatToolMode { RequiredFunctionName: { } name } => $"RequireSpecific({name})",
        _ => mode.GetType().Name,
    };

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
