// ============================================================================
// Skill lifecycle: validate, approve, pin per run, withdraw
// ----------------------------------------------------------------------------
// A SKILL.md is code the model follows. Edit it and the agent's behaviour
// changes on the next run, with no review. This project puts a gate between
// the skill folder and the provider:
//
//   SkillRegistry.ValidateAsync   owner + version in the frontmatter metadata,
//                                 every referenced file present, SHA-256 of the
//                                 effective content (every file, LF-normalized)
//   SkillRegistry.Approve         records it in skills.lock.json
//   GatedSkillsSource             a DelegatingAgentSkillsSource: activates a
//                                 skill only when its current hash matches the
//                                 approved record, pins it to that hash for the
//                                 run, and logs every operation with the hash
//   SkillRegistry.Withdraw        emergency stop, effective on the next
//                                 operation, in-flight runs included
//
//   dotnet run -- --approve [who] validate skills/ and write skills.lock.json
//   dotnet run                    one gated run against the configured provider
//   dotnet run -- --self-test     offline checks with a scripted client
// ============================================================================

using AgenticAI.Common.Extensions;
using Ch16.SkillLifecycle;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

if (args.Contains("--self-test")) return await SelfTest.RunAsync();

// dotnet run starts in the project folder, so these are the files you edit.
string skillsRoot = Path.Combine(Directory.GetCurrentDirectory(), "skills");
string lockFile = Path.Combine(Directory.GetCurrentDirectory(), "skills.lock.json");
AgentFileSkillsSource files = new(skillsRoot);

if (args.Contains("--approve"))
{
    // dotnet run -- --approve <reviewer>: the name recorded in the lock file.
    int at = Array.IndexOf(args, "--approve");
    string reviewer = at + 1 < args.Length ? args[at + 1] : "reviewer";
    SkillRegistry fresh = SkillRegistry.Load(lockFile);
    foreach (AgentSkill skill in await SkillDiscovery.FromFolderAsync(skillsRoot))
    {
        ValidationResult result = await SkillRegistry.ValidateAsync(skill);
        Console.WriteLine(result.Ok
            ? $"APPROVED {result.Record!.Name} v{result.Record.Version} (owner {result.Record.Owner}) sha256 {result.Record.Sha256}"
            : $"REFUSED  {skill.Frontmatter.Name}: {string.Join("; ", result.Problems)}");
        if (result.Ok) fresh.Approve(result, approvedBy: reviewer);
    }
    fresh.Save(lockFile);
    return 0;
}

HostApplicationBuilder hostBuilder = Host.CreateApplicationBuilder(args);
hostBuilder.Services.AddLlmChatClient(hostBuilder.Configuration, attemptTimeout: TimeSpan.FromMinutes(8));
using IHost host = hostBuilder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine("=== Skill lifecycle (gated run) ===");
Console.WriteLine();

GatedSkillsSource gated = new(files, SkillRegistry.Load(lockFile));
HarnessAgent agent = chatClient.AsHarnessAgent(new HarnessAgentOptions
{
    Name = "PolicyDesk",
    HarnessInstructions =
        "You answer Contoso refund questions. Load the refund-escalation skill with load_skill, " +
        "read the resource it names with read_skill_resource, then answer in two sentences.",
    AgentSkillsSource = gated,
    ToolApprovalAgentOptions = new ToolApprovalAgentOptions
    {
        AutoApprovalRules = [AgentSkillsProvider.ReadOnlyToolsAutoApprovalRule],
    },
    DisableWebSearch = true, DisableFileMemory = true,
    DisableTodoProvider = true, DisableAgentModeProvider = true,
    MaximumIterationsPerRequest = 6,
});

const string question = "A customer wants a $620 refund on a keyboard delivered 12 days ago. Can the desk approve it?";
Console.WriteLine($"You: {question}");
AgentResponse response;
using (gated.BeginRun("run-1"))   // one operation: every call in it sees the same pinned skills
{
    response = await agent.RunAsync(question, await agent.CreateSessionAsync(cts.Token), cancellationToken: cts.Token);
}
Console.WriteLine($"Agent: {response.Text.Trim()}");
Console.WriteLine();
Console.WriteLine("Run log:");
foreach (SkillEvent e in gated.Log)
{
    Console.WriteLine($"  {e.Run}  {e.Skill,-18} {e.Operation,-28} {e.Sha256[..12]}  {e.Detail}");
}
Console.WriteLine();
Console.WriteLine("=== Skill lifecycle demo complete! ===");
return 0;
