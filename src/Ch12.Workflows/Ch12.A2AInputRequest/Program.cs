// ============================================================================
// Chapter 12 — A2A input-request: human-in-the-loop across process boundaries
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
// This program demonstrates the workflow side. It uses the same RequestPort
// pattern from Ch12.ApprovalWorkflow but configures the workflow to surface
// the request over A2A rather than via local Console.ReadLine. The remote
// A2A server lives in Ch13.A2APreview; you wire that one separately.
// ============================================================================

using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgenticAI.Common.Extensions;

HostApplicationBuilder hostBuilder = Host.CreateApplicationBuilder(args);
hostBuilder.Services.AddLlmChatClient(hostBuilder.Configuration);

using IHost host = hostBuilder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine("=== Chapter 12: A2A input-request human-in-the-loop ===");
Console.WriteLine();

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

Workflow workflow = new WorkflowBuilder(drafterNode)
    .AddEdge(drafterNode, approvalNode)
    .AddEdge(approvalNode, finaliseNode)
    .WithOutputFrom(finaliseNode)
    .Build();

// ── Run with a streaming environment so we can intercept the RequestInfoEvent ─
string complaint =
    "I ordered an ergonomic keyboard 9 days ago and the order page still shows " +
    "\"processing\". Can you tell me when it will ship?";

Console.WriteLine($"Customer complaint: {complaint}");
Console.WriteLine();

StreamingRun stream = await InProcessExecution.RunStreamingAsync(
    workflow, complaint,
    sessionId: Guid.NewGuid().ToString(),
    cts.Token);

await foreach (WorkflowEvent evt in stream.WatchStreamAsync(cts.Token))
{
    switch (evt)
    {
        case RequestInfoEvent requestInfo:
            // This RequestPort can be wired to surface the request over A2A
            // instead of (or in addition to) the local Console.ReadLine path
            // used in Ch12.ApprovalWorkflow. The A2A input-request flow:
            //
            //   1. A2A server hosting this workflow exposes the request to the
            //      calling agent as an A2A input-request message.
            //   2. Caller (human or another agent) responds via A2A.
            //   3. Response is funneled back to the RequestPort here.
            //
            // For this single-process demo we approve immediately.
            if (requestInfo.Request.TryGetDataAs<string>(out string? draft))
            {
                Console.WriteLine("=== HUMAN APPROVAL REQUIRED (would surface via A2A) ===");
                Console.WriteLine($"Draft: {draft}");
                Console.WriteLine();
                Console.WriteLine("Auto-approving for the single-process demo.");
                ExternalResponse approvalResponse = requestInfo.Request.CreateResponse(draft ?? "");
                await stream.SendResponseAsync(approvalResponse);
            }
            break;

        case WorkflowOutputEvent output:
            Console.WriteLine("=== Final ===");
            Console.WriteLine(output.Data);
            break;
    }
}

Console.WriteLine();
Console.WriteLine("=== Done ===");

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
