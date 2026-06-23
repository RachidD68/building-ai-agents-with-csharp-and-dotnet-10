// ============================================================================
// Chapter 11 — Streaming a pipeline (MAF 1.10)
// ----------------------------------------------------------------------------
// Blocking RunAsync hides everything until the whole pipeline finishes. Real UX
// needs two things from a multi-stage pipeline:
//   1. intermediate progress ("Outlining... Drafting... Polishing...") so the
//      user knows work is happening, and
//   2. the FINAL stage's tokens streamed live as they are produced.
//
// This demo chains three agents by hand (the manual style from earlier in the
// chapter) so we control exactly what the user sees:
//   - intermediate stages run with blocking RunAsync and only emit a progress
//     line — their output is plumbing, not something the user reads token by
//     token;
//   - the final stage uses RunStreamingAsync, whose IAsyncEnumerable<
//     AgentResponseUpdate> we write to the console as each chunk arrives.
//
// RunStreamingAsync is the same per-agent streaming API from Chapter 5; here it
// drives the last stage of a pipeline.
// ============================================================================

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgenticAI.Common.Extensions;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);

using IHost host = builder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine("=== Chapter 11: Streaming a pipeline ===");
Console.WriteLine();

ChatClientAgent outliner = new(
    chatClient,
    instructions: """
        You are an outliner. Given a topic, produce a tight 3-bullet outline.
        Bullets only, no prose.
        """,
    name: "Outliner",
    description: "Produces a short outline from a topic.");

ChatClientAgent drafter = new(
    chatClient,
    instructions: """
        You are a drafter. Expand the outline into a short draft of about
        120 words. Plain, direct prose.
        """,
    name: "Drafter",
    description: "Drafts prose from an outline.");

ChatClientAgent polisher = new(
    chatClient,
    instructions: """
        You are a line editor. Tighten the draft into a final 90-word version.
        Return only the final text.
        """,
    name: "Polisher",
    description: "Polishes the draft into the final text.");

const string topic = "Why integration tests catch bugs unit tests miss.";
Console.WriteLine($"Topic: {topic}");
Console.WriteLine();

// ── Stage 1: outline (silent; show progress only) ───────────────────
Console.Write("Outlining... ");
AgentSession outlineSession = await outliner.CreateSessionAsync(cts.Token);
AgentResponse outline = await outliner.RunAsync(topic, outlineSession, cancellationToken: cts.Token);
Console.WriteLine("done.");

// ── Stage 2: draft (silent; show progress only) ─────────────────────
Console.Write("Drafting...  ");
AgentSession draftSession = await drafter.CreateSessionAsync(cts.Token);
AgentResponse draft = await drafter.RunAsync(outline.Text, draftSession, cancellationToken: cts.Token);
Console.WriteLine("done.");

// ── Stage 3: polish (FINAL stage; stream tokens live) ───────────────
Console.WriteLine("Polishing... (streaming)");
Console.WriteLine();
Console.WriteLine("--- Final text ---");

AgentSession polishSession = await polisher.CreateSessionAsync(cts.Token);
await foreach (AgentResponseUpdate update in
    polisher.RunStreamingAsync(draft.Text, polishSession, cancellationToken: cts.Token))
{
    Console.Write(update.Text);
}

Console.WriteLine();
Console.WriteLine();
Console.WriteLine("=== Done ===");
