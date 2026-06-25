// ============================================================================
// Chapter 13 — Introduction to Workflows (Approval Workflow — Capstone)
// Demonstrates a human-in-the-loop approval workflow using RequestPort.
//
// Key concepts:
//   - Class-based executors: agent-backed nodes derive from Executor<TIn, TOut>.
//   - RequestPort: pauses workflow execution for external input.
//   - StreamingRun: enables interaction with a running workflow.
//   - RequestInfoEvent: signals that the workflow needs human input.
//   - ExternalRequest.CreateResponse(): sends human decisions back.
//   - Revision loop: rejected drafts loop back for improvement.
//
// The workflow:
//   Topic → Drafter → Validator → [RequestPort: Human Approval]
//     ↳ Approved → Finalizer → Output
//     ↳ Rejected → Revision → Drafter (with feedback) → Validator → ...
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

Console.WriteLine("=== Chapter 12: Approval Workflow — Human-in-the-Loop ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// ---------------------------------------------------------------
// Step 1: Create the drafter and validator agents
// ---------------------------------------------------------------
ChatClientAgent drafterAgent = new(
    chatClient,
    instructions: """
        You are a professional document drafter. When given a topic (and
        optionally revision feedback), write a concise document (2-3 paragraphs).
        If feedback is provided, incorporate the suggested changes.
        Output ONLY the document text, nothing else.
        """,
    name: "Drafter",
    description: "Drafts documents based on topics and revision feedback");

ChatClientAgent validatorAgent = new(
    chatClient,
    instructions: """
        You are a document quality validator. Review the provided document for:
        - Completeness: does it cover the topic adequately?
        - Clarity: is the writing clear and well-structured?
        - Quality: is the grammar and style professional?

        Output a brief quality assessment in this exact format:
        SCORE: [1-10]
        ISSUES: [comma-separated list of issues, or "none"]
        SUMMARY: [one-sentence assessment]
        """,
    name: "Validator",
    description: "Validates document quality and identifies issues");

// ---------------------------------------------------------------
// Step 2: Build the workflow with human-in-the-loop
// ---------------------------------------------------------------
// The RequestPort pauses the workflow and emits a RequestInfoEvent.
// We read the request from the streaming events, prompt the user via
// Console.ReadLine, and send the response back to the workflow.

RequestPort approvalPort = RequestPort.Create<ValidationResult, string>("human-approval");

ExecutorBinding drafterNode = new DrafterExecutor(drafterAgent).BindExecutor();
ExecutorBinding validatorNode = new ValidatorExecutor(validatorAgent).BindExecutor();
ExecutorBinding approvalNode = approvalPort.BindAsExecutor();
ExecutorBinding finalizerNode = new FinalizerExecutor().BindExecutor();
ExecutorBinding revisionNode = new RevisionExecutor().BindExecutor();

// The RequestPort's response is a string. On "revise" the workflow loops back;
// any other response is treated as the approved draft text and flows to the
// finalizer. (This mirrors an "edit then approve" review, where the human can
// tweak the text before accepting it.)
Workflow approvalWorkflow = new WorkflowBuilder(drafterNode)
    .AddEdge(drafterNode, validatorNode)
    .AddEdge(validatorNode, approvalNode)
    .AddEdge<string>(approvalNode, revisionNode,
        decision => decision is not null && decision.Equals("revise", StringComparison.OrdinalIgnoreCase))
    .AddEdge<string>(approvalNode, finalizerNode,
        decision => decision is not null && !decision.Equals("revise", StringComparison.OrdinalIgnoreCase))
    .AddEdge(revisionNode, drafterNode)
    .WithOutputFrom(finalizerNode)
    .WithName("ApprovalWorkflow")
    .WithDescription("Document drafting with human-in-the-loop approval")
    .Build();

// ---------------------------------------------------------------
// Step 3: Visualize the workflow
// ---------------------------------------------------------------
Console.WriteLine("--- Workflow Graph (Mermaid) ---");
Console.WriteLine(WorkflowVisualizer.ToMermaidString(approvalWorkflow));
Console.WriteLine();

// ---------------------------------------------------------------
// Step 4: Run the workflow with streaming (for HITL interaction)
// ---------------------------------------------------------------
string topic = "Write a brief overview of how multi-agent AI systems " +
               "are changing software development practices.";

Console.WriteLine($"[TOPIC] {topic}\n");

StreamingRun stream = await InProcessExecution.RunStreamingAsync(
    approvalWorkflow, topic, sessionId: Guid.NewGuid().ToString(), cts.Token);

// Process streaming events and handle human input requests.
await foreach (WorkflowEvent evt in stream.WatchStreamAsync(cts.Token))
{
    switch (evt)
    {
        case RequestInfoEvent requestInfo:
            // The workflow is paused, waiting for human input.
            ExternalRequest request = requestInfo.Request;

            if (request.TryGetDataAs<ValidationResult>(out ValidationResult? validationData))
            {
                Console.WriteLine("\n" + new string('=', 60));
                Console.WriteLine("HUMAN REVIEW REQUIRED");
                Console.WriteLine(new string('=', 60));
                Console.WriteLine($"\nDraft:\n{validationData.Draft}");
                Console.WriteLine($"\nValidation:\n{validationData.Assessment}");
                Console.WriteLine();
                Console.WriteLine("Enter 'approve' to accept or 'revise' to request changes:");
                Console.Write("> ");

                string? decision = Console.ReadLine()?.Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(decision) || (decision != "approve" && decision != "revise"))
                {
                    decision = "approve"; // Default to approve.
                    Console.WriteLine("  (Defaulting to 'approve')");
                }

                // On approve, send the draft itself downstream to the finalizer;
                // on revise, send "revise" so the workflow loops back.
                string reply = decision == "revise" ? "revise" : validationData.Draft;
                ExternalResponse response = request.CreateResponse(reply);
                await stream.SendResponseAsync(response);
                Console.WriteLine($"  Decision sent: {decision}\n");
            }
            break;

        case WorkflowOutputEvent output:
            Console.WriteLine("\n" + new string('=', 60));
            Console.WriteLine("WORKFLOW COMPLETE");
            Console.WriteLine(new string('=', 60));
            Console.WriteLine(output.Data);
            break;

        case WorkflowErrorEvent error:
            Console.WriteLine($"\n  [ERROR] {error.Data}");
            break;
    }
}

Console.WriteLine("\n=== Approval Workflow demo complete! ===");

// ---------------------------------------------------------------
// Executors
// ---------------------------------------------------------------

/// <summary>Drafts a document using the LLM agent.</summary>
internal sealed class DrafterExecutor(AIAgent agent) : Executor<string, string>("drafter")
{
    public override async ValueTask<string> HandleAsync(
        string input, IWorkflowContext context, CancellationToken ct)
    {
        Console.WriteLine("\n  [Drafter] Creating draft...");
        AgentSession session = await agent.CreateSessionAsync(ct);
        AgentResponse response = await agent.RunAsync(input, session, cancellationToken: ct);
        string draft = response.Text;
        Console.WriteLine($"  [Drafter] Draft complete ({draft.Length} chars)");
        return draft;
    }
}

/// <summary>Validates the draft using the LLM agent.</summary>
internal sealed class ValidatorExecutor(AIAgent agent) : Executor<string, ValidationResult>("validator")
{
    public override async ValueTask<ValidationResult> HandleAsync(
        string draft, IWorkflowContext context, CancellationToken ct)
    {
        Console.WriteLine("  [Validator] Reviewing draft...");
        AgentSession session = await agent.CreateSessionAsync(ct);
        AgentResponse response = await agent.RunAsync(draft, session, cancellationToken: ct);
        string assessment = response.Text;
        Console.WriteLine($"  [Validator] {assessment}");
        return new ValidationResult(draft, assessment);
    }
}

/// <summary>Formats the final approved document and yields it as workflow output.</summary>
[YieldsOutput(typeof(string))]
internal sealed class FinalizerExecutor() : Executor<string>("finalizer")
{
    public override async ValueTask HandleAsync(
        string approvedDraft, IWorkflowContext context, CancellationToken ct)
    {
        string final = $"=== APPROVED DOCUMENT ===\n\n{approvedDraft}";
        Console.WriteLine("  [Finalizer] Document approved and finalized!");
        await context.YieldOutputAsync(final, ct);
    }
}

/// <summary>
/// Formats rejection feedback for the revision loop. The RequestPort hands the
/// human's decision string downstream; on "revise" this executor turns it into
/// the next drafting prompt. (The full assessment is held by the validator's
/// last output; here we package a revision instruction for the drafter.)
/// </summary>
internal sealed class RevisionExecutor() : Executor<string, string>("revision")
{
    public override ValueTask<string> HandleAsync(
        string decision, IWorkflowContext context, CancellationToken ct)
    {
        string feedback = "Please revise the previous document to address the " +
                          "validator's feedback, improving clarity and completeness.";
        Console.WriteLine("  [Revision] Sending feedback to drafter for revision...");
        return ValueTask.FromResult(feedback);
    }
}

// ---------------------------------------------------------------
// Types
// ---------------------------------------------------------------

/// <summary>
/// Carries a document draft together with its validation assessment
/// through the workflow pipeline.
/// </summary>
/// <param name="Draft">The document draft text.</param>
/// <param name="Assessment">The validator's quality assessment.</param>
internal sealed record ValidationResult(string Draft, string Assessment);
