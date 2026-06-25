// ============================================================================
// Chapter 8 — FileAccessProvider: a sandboxed scratchpad for your agent
// ----------------------------------------------------------------------------
// MAF 1.11 ships a built-in context provider that gives an agent a SAFE, scoped
// filesystem: FileAccessProvider. Register it and the model gains six tools —
// read_file, save_file, delete_file, list_files, list_subdirectories, and
// search_files — all confined to a store you choose. No System.IO, no path the
// model can wander out of.
//
// Two pieces:
//   1. An AgentFileStore — WHERE the files live. InMemoryAgentFileStore (a
//      dictionary, used here) for demos/tests; FileSystemAgentFileStore(rootPath)
//      for a real disk folder (symlink-guarded, so the agent can't escape it).
//   2. FileAccessProvider(store) — registered on ChatClientAgentOptions
//      .AIContextProviders, it exposes the six tools over that store.
//
// SAFETY (the 1.11 headline): file tools are gated. We wrap the agent in a
// ToolApprovalAgent and pass FileAccessProvider.ReadOnlyToolsAutoApprovalRule,
// which waves through the read-only tools (read/list/search) but still pauses
// on save and delete. That boundary — reads are cheap, writes need a human — is
// the whole point. Cross-reference: Chapter 16 (Safety and Guardrails).
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

Console.WriteLine("=== Chapter 8: FileAccessProvider — a sandboxed scratchpad ===\n");

// ── Step 1: a store, seeded directly via its API ─────────────────────────────
// InMemoryAgentFileStore keeps files in a dictionary — perfect for a demo and
// for token-free tests. Swap in FileSystemAgentFileStore(rootPath) for a real
// folder. The store API (WriteFileAsync/ReadFileAsync/ListFilesAsync/...) is the
// same surface the six tools call underneath.
InMemoryAgentFileStore store = new();
await store.WriteFileAsync("notes.txt",
    "Customer asked about the refund policy. Promised a follow-up by Friday.", cts.Token);
await store.WriteFileAsync("policy.md",
    "# Refund Policy\nRefunds within 30 days with a receipt. No restocking fee under $50.", cts.Token);

Console.WriteLine("Seeded the store with: notes.txt, policy.md\n");

// ── Step 2: the provider exposes six file tools over that store ──────────────
FileAccessProvider files = new(store);

// ── Step 3: the agent — register the provider, then GATE it ──────────────────
// AIContextProviders is where context providers (RAG, memory, file access) plug
// in — exactly the slot the Chapter 10 custom provider used.
ChatClientAgent baseAgent = new(chatClient, new ChatClientAgentOptions
{
    Name = "ScratchpadAgent",
    ChatOptions = new ChatOptions
    {
        Instructions =
            "You manage a small set of notes for the user. Use the file tools to "
            + "list, read, search, and save files. Always call a tool rather than "
            + "guessing about the files.",
        AllowMultipleToolCalls = false,
    },
    AIContextProviders = [files],
});

// Wrap with approval. ReadOnlyToolsAutoApprovalRule auto-approves read/list/
// search; save and delete would still pause for a human (see Chapter 8's
// tool-approval sample for the approve/reject loop, and Chapter 16 for why).
AIAgent agent = baseAgent.AsBuilder()
    .UseToolApproval(new ToolApprovalAgentOptions
    {
        AutoApprovalRules = [FileAccessProvider.ReadOnlyToolsAutoApprovalRule],
    })
    .Build();

// ── A read-only request: the model calls list_files / read_file, the rule waves
//    them through with no prompt, and the agent answers from the store. ────────
try
{
    AgentSession session = await agent.CreateSessionAsync(cts.Token);

    const string prompt = "What files do I have, and what does notes.txt say?";
    Console.WriteLine($"User : {prompt}");

    AgentResponse response = await agent.RunAsync(prompt, session, cancellationToken: cts.Token);
    Console.WriteLine($"Agent: {response.Text}\n");

    Console.WriteLine("=== FileAccess demo complete! ===");
    Console.WriteLine("(A 'save this summary to summary.txt' request would PAUSE for approval —");
    Console.WriteLine(" read-only tools auto-approve, writes do not.)");
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n[Cancelled by user]");
}
catch (Exception ex)
{
    Console.WriteLine($"\n[error] {ex.Message}");
    Console.WriteLine("This demo needs a tool-calling model. The default Ollama llama3.2 supports");
    Console.WriteLine("tools; a cloud gpt-4o-class model is the most reliable for multi-tool flows.");
}
