using AgenticAI.Testing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

/// <summary>Offline checks: dotnet run -- --self-test. A scripted client asks for the file tool calls.</summary>
static class SelfTest
{
    public static async Task<int> RunAsync()
    {
        Console.WriteLine("=== Harness files self-test (offline) ===\n");
        int passed = 0, total = 0;
        void Check(string name, bool ok)
        {
            total++; passed += ok ? 1 : 0;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
        }

        string original = Directory.GetCurrentDirectory();
        string work = Path.Combine(Path.GetTempPath(), "ch16-files-" + Guid.NewGuid().ToString("N")[..8]);
        string cwd = Path.Combine(work, "cwd"), workspace = Path.Combine(work, "workspace"), memory = Path.Combine(work, "memory");
        foreach (string d in new[] { cwd, workspace, memory }) Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(workspace, "refund-policy.md"), "Refunds are available within 30 days of delivery.");
        File.WriteAllText(Path.Combine(work, "secret.txt"), "outside the workspace");
        Directory.SetCurrentDirectory(cwd);
        try
        {
            // 1. Memory with no store given: where does the note land?
            AgentResponse note = await RunAsync(Options(memoryStore: null), FileMemoryProvider.WriteToolName, Note());
            Check("DEFECT REPRODUCED: with no FileMemoryStore, a memory note is written under {cwd}/agent-file-memory",
                Directory.GetFiles(Path.Combine(cwd, "agent-file-memory"), "note.md", SearchOption.AllDirectories).Length == 1);
            Check("file_memory_write ran with no approval request (the agent's own scratch space)",
                !Requests(note).Any());
            Directory.Delete(Path.Combine(cwd, "agent-file-memory"), recursive: true);

            // 2. The fix: an explicit memory store.
            await RunAsync(Options(new FileSystemAgentFileStore(memory)), FileMemoryProvider.WriteToolName, Note());
            Check("with FileMemoryStore set, the note lands in that store and nothing under the working directory",
                Directory.GetFiles(memory, "note.md", SearchOption.AllDirectories).Length == 1 && !Directory.EnumerateFileSystemEntries(cwd).Any());

            // 3. Access is off unless you pass a store.
            ScriptedToolChatClient probe = new(_ => ScriptedToolChatClient.Say("ok"));
            HarnessAgent noAccess = probe.AsHarnessAgent(Options(new FileSystemAgentFileStore(memory), access: false));
            await noAccess.RunAsync("hi", await noAccess.CreateSessionAsync());
            Check("FileAccessStore left null: no file_access_* tool is sent",
                !probe.Turns[0].ToolNames.Any(t => t.StartsWith("file_access_")));

            // 4. Reads flow through the read-only rule.
            AgentResponse read = await RunAsync(Options(new FileSystemAgentFileStore(memory)),
                FileAccessProvider.ReadFileToolName, FileArg("refund-policy.md"));
            Check("file_access_read is auto-approved by FileAccessProvider.ReadOnlyToolsAutoApprovalRule and returns the file",
                !Requests(read).Any() && Results(read).Any(r => r.Contains("30 days")));

            // 5. A write stops for a human.
            Dictionary<string, object?> write = new() { ["fileName"] = "notes.md", ["content"] = "draft" };
            (HarnessAgent writer, AgentSession session, AgentResponse paused) =
                await StartAsync(Options(new FileSystemAgentFileStore(memory)), FileAccessProvider.WriteToolName, write);
            ToolApprovalRequestContent? request = Requests(paused).SingleOrDefault();
            Check("file_access_write pauses with a ToolApprovalRequestContent and writes nothing",
                request is not null && !File.Exists(Path.Combine(workspace, "notes.md")));

            // 6. Rejected: still nothing written. (Approval binding is the authorized-actions chapter's rule.)
            await writer.RunAsync([new ChatMessage(ChatRole.User, [request!.CreateResponse(approved: false, reason: "not now")])], session);
            Check("a rejected write leaves the workspace unchanged", !File.Exists(Path.Combine(workspace, "notes.md")));

            // 7. Approved on a fresh request: written inside the workspace.
            (writer, session, paused) = await StartAsync(Options(new FileSystemAgentFileStore(memory)), FileAccessProvider.WriteToolName, write);
            await writer.RunAsync([new ChatMessage(ChatRole.User, [Requests(paused).Single().CreateResponse(approved: true)])], session);
            Check("an approved write lands in the workspace", File.Exists(Path.Combine(workspace, "notes.md")));

            // 8. Hostile path: a read that climbs out of the workspace.
            AgentResponse escape = await RunAsync(Options(new FileSystemAgentFileStore(memory)),
                FileAccessProvider.ReadFileToolName, FileArg("../secret.txt"));
            Check("file_access_read of ../secret.txt does not return the file outside the workspace",
                !Results(escape).Any(r => r.Contains("outside the workspace")));
        }
        finally
        {
            Directory.SetCurrentDirectory(original);
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }

        Console.WriteLine($"\nRESULT {passed}/{total}");
        return passed == total ? 0 : 1;

        HarnessAgentOptions Options(AgentFileStore? memoryStore, bool access = true) => new()
        {
            FileMemoryStore = memoryStore,
            FileAccessStore = access ? new FileSystemAgentFileStore(workspace) : null,
            ToolApprovalAgentOptions = new ToolApprovalAgentOptions
            {
                AutoApprovalRules = [FileAccessProvider.ReadOnlyToolsAutoApprovalRule],
            },
            DisableWebSearch = true, DisableTodoProvider = true,
            DisableAgentModeProvider = true, DisableAgentSkillsProvider = true,
        };
    }

    private static Dictionary<string, object?> Note() => new() { ["fileName"] = "note.md", ["content"] = "customer prefers email" };
    private static Dictionary<string, object?> FileArg(string name) => new() { ["fileName"] = name };

    private static IEnumerable<ToolApprovalRequestContent> Requests(AgentResponse r) =>
        r.Messages.SelectMany(m => m.Contents).OfType<ToolApprovalRequestContent>();

    private static IEnumerable<string> Results(AgentResponse r) =>
        r.Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(f => f.Result?.ToString() ?? "");

    private static async Task<AgentResponse> RunAsync(HarnessAgentOptions options, string tool, Dictionary<string, object?> args) =>
        (await StartAsync(options, tool, args)).Response;

    private static async Task<(HarnessAgent Agent, AgentSession Session, AgentResponse Response)> StartAsync(
        HarnessAgentOptions options, string tool, Dictionary<string, object?> args)
    {
        ScriptedToolChatClient client = null!;
        client = new(turn => turn.Results.Count == 0 ? client.Call(tool, args) : ScriptedToolChatClient.Say("done"));
        HarnessAgent agent = client.AsHarnessAgent(options);
        AgentSession session = await agent.CreateSessionAsync();
        return (agent, session, await agent.RunAsync("Do the file task.", session));
    }
}
