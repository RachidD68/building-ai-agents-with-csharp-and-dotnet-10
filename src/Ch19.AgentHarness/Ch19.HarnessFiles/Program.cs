// ============================================================================
// Chapter 19 — Files in the harness: memory versus access
// ----------------------------------------------------------------------------
// The harness has two different file stories and they are easy to confuse:
//
//   FileMemoryStore  (on by default)  -- the agent's OWN scratch space. It
//       writes notes it wants to survive compaction. Default location is
//       {cwd}/agent-file-memory/{timestamp}_{guid}, so a default run leaves
//       a directory behind. Set DisableFileMemory to stop it, or point
//       FileMemoryStore somewhere you control.
//
//   FileAccessStore  (OFF by default) -- YOUR files, exposed to the agent as
//       read/write/search tools. Opting in is a deliberate act: null means no
//       file tools at all. Pair it with FileAccessProvider's
//       ReadOnlyToolsAutoApprovalRule so reads flow and writes stop for a human.
//
// Both properties are [Experimental("MAAI001")] at 1.20; the csproj scopes the
// suppression to this project.
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

Console.WriteLine("=== Chapter 19: Harness files — memory vs access ===");
Console.WriteLine();

// ── A small workspace the agent is allowed to read ────────────────────
string workspace = Path.Combine(AppContext.BaseDirectory, "workspace");
Directory.CreateDirectory(workspace);
await File.WriteAllTextAsync(Path.Combine(workspace, "refund-policy.md"), """
    # Contoso refund policy

    - Refunds are available within 30 days of delivery.
    - Orders over $500 require a manager's approval.
    - Digital goods are non-refundable once downloaded.
    """, cts.Token);
await File.WriteAllTextAsync(Path.Combine(workspace, "shipping-policy.md"), """
    # Contoso shipping policy

    - Standard shipping is 3-5 business days.
    - Expedited shipping is next business day before 3pm.
    """, cts.Token);

// Two separate stores, two separate jobs.
string memoryRoot = Path.Combine(AppContext.BaseDirectory, "agent-memory");
Directory.CreateDirectory(memoryRoot);

FileSystemAgentFileStore accessStore = new(workspace);    // YOUR documents
FileSystemAgentFileStore memoryStore = new(memoryRoot);   // the agent's scratch space

Console.WriteLine($"FileAccessStore (your files)   : {workspace}");
Console.WriteLine($"FileMemoryStore (agent's own)  : {memoryRoot}");
Console.WriteLine();

HarnessAgentOptions options = new()
{
    Name = "PolicyDesk",
    HarnessInstructions =
        "You answer Contoso policy questions strictly from the files in your workspace. " +
        "Search or read the files before answering. Never guess a policy.",
    ChatOptions = new ChatOptions { Instructions = "Answer in two sentences at most." },

    // Opt in to file access, and pin memory somewhere explicit instead of
    // letting it default into the working directory.
    FileAccessStore = accessStore,
    FileMemoryStore = memoryStore,

    // Reads flow through untouched; writes and deletes still surface for a
    // human. This is the rule the framework ships for exactly this shape.
    ToolApprovalAgentOptions = new ToolApprovalAgentOptions
    {
        AutoApprovalRules = [FileAccessProvider.ReadOnlyToolsAutoApprovalRule],
    },

    DisableWebSearch = true,
    DisableTodoProvider = true,
    DisableAgentModeProvider = true,
};

HarnessAgent agent = chatClient.AsHarnessAgent(options);
AgentSession session = await agent.CreateSessionAsync(cts.Token);

Console.WriteLine("Auto-approval: read-only file tools only (writes would pause for a human).");
Console.WriteLine();

string[] questions =
[
    "How long do I have to request a refund?",
    "How fast is expedited shipping?",
];

foreach (string q in questions)
{
    Console.WriteLine($"You: {q}");
    Console.Write("Agent: ");
    await foreach (AgentResponseUpdate update in
        agent.RunStreamingAsync(q, session, cancellationToken: cts.Token))
    {
        Console.Write(update.Text);
    }
    Console.WriteLine();
    Console.WriteLine();
}

// Show what the agent left behind in its own memory store, if anything.
string[] memoryFiles = Directory.Exists(memoryRoot)
    ? Directory.GetFiles(memoryRoot, "*", SearchOption.AllDirectories)
    : [];
Console.WriteLine($"Files in the agent's memory store after the run: {memoryFiles.Length}");
foreach (string f in memoryFiles.Take(5))
{
    Console.WriteLine($"  {Path.GetRelativePath(memoryRoot, f)}");
}
Console.WriteLine();
Console.WriteLine("=== Harness files demo complete! ===");
