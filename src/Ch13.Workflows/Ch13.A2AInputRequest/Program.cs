// ============================================================================
// Chapter 13 — A2A input-request: human-in-the-loop across process boundaries
// ----------------------------------------------------------------------------
// Earlier in this chapter the ApprovalWorkflow used a RequestPort to pause for
// a human decision inside a single process. MAF generalises that pattern: an
// A2A agent server can pause and send an "input-request" message back to the
// calling agent. The caller resumes the original turn once it has the answer —
// either from a real human or another agent.
//
// The shape:
//   workflow hits RequestPort
//     ─► emits A2A input-request to the remote server
//        ─► remote server displays the question to a human (or another agent)
//        ─► human replies; reply travels back through A2A
//     ◄─ workflow resumes with the response
//
// This program shows the workflow side only, in a single process. It uses the
// same RequestPort pattern as Ch13.ApprovalWorkflow and prints the request
// that would be surfaced over A2A, but nothing here sends it over A2A, and it
// does not read Console.ReadLine either. The remote A2A server lives in
// Ch14.A2APreview; you wire that one separately.
//
// Because nobody answers over A2A here, the decision is SIMULATED: a fixed,
// typed ApprovalDecision, printed as such. Over A2A, the caller's reply would
// decide. It fails closed: an empty draft is never approved (the demo stops
// with DEMO STOPPED).
//
// Offline check (approve, reject, empty draft): dotnet run -- --self-test
// ============================================================================

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgenticAI.Common.Extensions;

if (args.Contains("--self-test")) return await A2ASelfTest.RunAsync();

HostApplicationBuilder hostBuilder = Host.CreateApplicationBuilder(args);
hostBuilder.Services.AddLlmChatClient(hostBuilder.Configuration);

using IHost host = hostBuilder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine("=== Chapter 13: A2A input-request human-in-the-loop ===");
Console.WriteLine();

// ── Run with a streaming environment so we can intercept the RequestInfoEvent ─
string complaint =
    "I ordered an ergonomic keyboard 9 days ago and the order page still shows " +
    "\"processing\". Can you tell me when it will ship?";

Console.WriteLine($"Customer complaint: {complaint}");
Console.WriteLine();

// The decision a remote reviewer would send back over A2A. Nobody is on the
// other end in this demo, so it is fixed here and labelled SIMULATED.
const ApprovalDecision SimulatedDecision = ApprovalDecision.Approve;

ReviewOutcome outcome = await SupportReview.RunAsync(
    chatClient, complaint, SimulatedDecision, Console.Out, cts.Token);

if (outcome.Stopped)
{
    Console.Error.WriteLine(
        "DEMO STOPPED: the draft is empty, so there is nothing to approve. Nothing was finalized.");
    return 2;
}

Console.WriteLine();
Console.WriteLine("=== Done ===");
return 0;

// ---------------------------------------------------------------
// The workflow and its run
// ---------------------------------------------------------------

/// <summary>What one review run produced: the workflow output, or a fail-closed stop.</summary>
internal sealed record ReviewOutcome(string? Output, bool Stopped);

internal static class SupportReview
{
    /// <summary>
    /// draft → RequestPort (human approval) → finalise, or → rejection.
    /// The <paramref name="decision"/> stands in for the reply that would come
    /// back over A2A.
    /// </summary>
    public static async Task<ReviewOutcome> RunAsync(
        IChatClient chatClient, string complaint, ApprovalDecision decision,
        TextWriter log, CancellationToken ct)
    {
        // ── A simple agent that drafts a customer-support response ─────────
        ChatClientAgent supportDrafter = new(
            chatClient,
            instructions: """
                You draft customer-support replies. The reply must be friendly,
                accurate, and short (3-5 sentences). Read the incoming complaint and
                return the draft reply text, nothing else.
                """,
            name: "SupportDrafter");

        // ── Workflow: draft → RequestPort (human approval) → finalise ──────
        RequestPort approvalPort = RequestPort.Create<string, string>("human-approval");

        ExecutorBinding drafterNode = new SupportDraftExecutor(supportDrafter).BindExecutor();
        ExecutorBinding approvalNode = approvalPort.BindAsExecutor();
        ExecutorBinding finaliseNode = new FinaliseExecutor().BindExecutor();
        ExecutorBinding rejectNode = new RejectionExecutor().BindExecutor();

        Workflow workflow = new WorkflowBuilder(drafterNode)
            .AddEdge(drafterNode, approvalNode)
            // An empty reply matches neither edge, so it can never reach the finaliser.
            .AddEdge<string>(approvalNode, finaliseNode,
                reply => !string.IsNullOrWhiteSpace(reply) && reply != ApprovalDecisions.RejectReply)
            .AddEdge<string>(approvalNode, rejectNode, reply => reply == ApprovalDecisions.RejectReply)
            .WithOutputFrom(finaliseNode, rejectNode)
            .Build();

        await using StreamingRun stream = await InProcessExecution.RunStreamingAsync(
            workflow, complaint,
            sessionId: Guid.NewGuid().ToString(),
            ct);

        string? result = null;
        await foreach (WorkflowEvent evt in stream.WatchStreamAsync(ct))
        {
            switch (evt)
            {
                case RequestInfoEvent requestInfo:
                    // This RequestPort can be wired to surface the request over A2A
                    // instead of (or in addition to) the local Console.ReadLine path
                    // used in Ch13.ApprovalWorkflow. The A2A input-request flow:
                    //
                    //   1. A2A server hosting this workflow exposes the request to the
                    //      calling agent as an A2A input-request message.
                    //   2. Caller (human or another agent) responds via A2A.
                    //   3. Response is funneled back to the RequestPort here.
                    //
                    // In this single-process demo the decision is SIMULATED.
                    if (requestInfo.Request.TryGetDataAs<string>(out string? draft))
                    {
                        log.WriteLine("=== HUMAN APPROVAL REQUIRED (would surface via A2A) ===");
                        log.WriteLine($"Draft: {draft}");
                        log.WriteLine();
                        log.WriteLine($"SIMULATED human decision: {decision.ToString().ToLowerInvariant()} " +
                                      "(single-process demo; over A2A the caller's reply decides).");
                        log.WriteLine();

                        // Fail closed: an approval needs a non-empty draft to approve.
                        if (!ApprovalDecisions.TryCreateReply(decision, draft, out string? reply))
                            return new ReviewOutcome(null, Stopped: true);

                        await stream.SendResponseAsync(requestInfo.Request.CreateResponse(reply));
                    }
                    break;

                case WorkflowOutputEvent output:
                    result = output.Data?.ToString();
                    log.WriteLine("=== Final ===");
                    log.WriteLine(result);
                    break;
            }
        }
        return new ReviewOutcome(result, Stopped: false);
    }
}

// ---------------------------------------------------------------
// The decision, typed. The same small helper as Ch13.CheckpointedApproval
// (each sample is its own executable, so it is repeated here).
// ---------------------------------------------------------------

/// <summary>The only two outcomes a reviewer can choose here.</summary>
internal enum ApprovalDecision { Approve, Reject }

internal static class ApprovalDecisions
{
    /// <summary>The reply that routes to the rejection handler.</summary>
    public const string RejectReply = "reject";

    /// <summary>
    /// Builds the RequestPort reply for a decision: the draft itself on approve,
    /// the reject word on reject. Fails closed: approving a null, empty or
    /// whitespace draft returns false and no reply, so nothing is approved by default.
    /// </summary>
    public static bool TryCreateReply(ApprovalDecision decision, string? draft, [NotNullWhen(true)] out string? reply)
    {
        reply = decision switch
        {
            ApprovalDecision.Reject => RejectReply,
            ApprovalDecision.Approve when !string.IsNullOrWhiteSpace(draft) => draft,
            _ => null,
        };
        return reply is not null;
    }
}

// ---------------------------------------------------------------
// Executors
// ---------------------------------------------------------------

/// <summary>Drafts a support reply using the LLM agent.</summary>
internal sealed class SupportDraftExecutor(AIAgent agent) : Executor<string, string>("drafter")
{
    public override async ValueTask<string> HandleAsync(
        string input, IWorkflowContext context, CancellationToken ct)
    {
        AgentSession session = await agent.CreateSessionAsync(ct);
        AgentResponse response = await agent.RunAsync(input, session, cancellationToken: ct);
        return response.Text ?? string.Empty;
    }
}

/// <summary>Formats the approved reply as workflow output.</summary>
[YieldsOutput(typeof(string))]
internal sealed class FinaliseExecutor() : Executor<string>("finalise")
{
    public override async ValueTask HandleAsync(
        string approved, IWorkflowContext context, CancellationToken ct)
        => await context.YieldOutputAsync($"[APPROVED]\n{approved}", ct);
}

/// <summary>Ends a rejected review: nothing is finalised.</summary>
[YieldsOutput(typeof(string))]
internal sealed class RejectionExecutor() : Executor<string>("rejection")
{
    public override async ValueTask HandleAsync(
        string decision, IWorkflowContext context, CancellationToken ct)
        => await context.YieldOutputAsync("[REJECTED] The draft was not sent.", ct);
}

// ---------------------------------------------------------------
// --self-test: offline. A scripted IChatClient stands in for the model,
// so the workflow runs deterministically without a provider.
// ---------------------------------------------------------------

/// <summary>A fake IChatClient that returns one fixed reply.</summary>
internal sealed class ScriptedChatClient(string reply) : IChatClient
{
    public const string Draft =
        "Thanks for your patience. Your keyboard order is being packed and should " +
        "ship within two business days. You will get a tracking link by email as soon as it leaves.";

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new ChatResponseUpdate(ChatRole.Assistant, reply);
        await Task.CompletedTask;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

internal static class A2ASelfTest
{
    private static int s_passed;
    private static int s_total;

    public static async Task<int> RunAsync()
    {
        Console.WriteLine("=== Chapter 13: A2A input-request — self-test (offline) ===\n");
        const string Complaint = "Where is my keyboard order?";
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));

        Check("approve + empty, whitespace or null draft -> no reply (fail closed)",
            !ApprovalDecisions.TryCreateReply(ApprovalDecision.Approve, "", out _) &&
            !ApprovalDecisions.TryCreateReply(ApprovalDecision.Approve, "   ", out _) &&
            !ApprovalDecisions.TryCreateReply(ApprovalDecision.Approve, null, out _));
        Check("approve + draft -> the draft; reject -> the reject word",
            ApprovalDecisions.TryCreateReply(ApprovalDecision.Approve, "text", out string? yes) && yes == "text" &&
            ApprovalDecisions.TryCreateReply(ApprovalDecision.Reject, null, out string? no) &&
            no == ApprovalDecisions.RejectReply);

        using ScriptedChatClient drafting = new(ScriptedChatClient.Draft);
        ReviewOutcome approved = await SupportReview.RunAsync(
            drafting, Complaint, ApprovalDecision.Approve, TextWriter.Null, timeout.Token);
        Check("workflow + approve -> [APPROVED] with the drafted text",
            !approved.Stopped && approved.Output is { } a &&
            a.StartsWith("[APPROVED]", StringComparison.Ordinal) && a.Contains(ScriptedChatClient.Draft));

        ReviewOutcome rejected = await SupportReview.RunAsync(
            drafting, Complaint, ApprovalDecision.Reject, TextWriter.Null, timeout.Token);
        Check("workflow + reject -> [REJECTED], nothing approved",
            !rejected.Stopped && rejected.Output is { } r &&
            r.StartsWith("[REJECTED]", StringComparison.Ordinal) && !r.Contains("[APPROVED]"));

        using ScriptedChatClient silent = new("");
        ReviewOutcome empty = await SupportReview.RunAsync(
            silent, Complaint, ApprovalDecision.Approve, TextWriter.Null, timeout.Token);
        Check("workflow + approve of an empty draft -> stopped, nothing finalised",
            empty.Stopped && empty.Output is null);

        Console.WriteLine($"\nRESULT {s_passed}/{s_total}");
        return s_passed == s_total ? 0 : 1;
    }

    private static void Check(string name, bool ok)
    {
        s_total++;
        if (ok) s_passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    }
}
