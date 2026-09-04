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
        "You answer Contoso policy questions strictly from files in your workspace. " +
        "The workspace contains exactly two files: refund-policy.md and " +
        "shipping-policy.md. ALWAYS call the file read tool on the relevant file " +
        "before answering, then quote the matching line. Never guess a policy.",
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

// The store is a plain object you can query yourself -- useful when you want
// to prove the agent's view of the world before you trust its answers.
IReadOnlyList<FileStoreEntry> visible = await accessStore.ListChildrenAsync("", cts.Token);
Console.WriteLine($"Files the agent can reach ({visible.Count}):");
foreach (FileStoreEntry entry in visible)
{
    Console.WriteLine($"  {entry.Name}  ({entry.Type})");
}

Console.WriteLine("Auto-approval: read-only file tools only (writes would pause for a human).");
Console.WriteLine();

// NOTE the shape of these questions. A frontier model infers which file to
// open from "how long do I have to request a refund?"; a small local model
// needs the filename in the sentence. Naming the file is not cheating -- it
// is what you do when your model is small, and it is why the instructions
// above list the workspace contents.
string[] questions =
[
    "Read the file refund-policy.md and tell me how many days I have to request a refund.",
    "Read the file shipping-policy.md and tell me how fast expedited shipping is.",
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
