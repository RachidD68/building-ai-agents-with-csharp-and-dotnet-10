using AgenticAI.Testing;
using Ch16.CodingAgent;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

/// <summary>Offline checks: dotnet run -- --self-test. No model, no Copilot call.</summary>
static class SelfTest
{
    public static async Task<int> RunAsync()
    {
        Console.WriteLine("=== Coding agent self-test (offline) ===\n");
        int passed = 0, total = 0;
        void Check(string name, bool ok)
        {
            total++; passed += ok ? 1 : 0;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
        }

        string work = Path.Combine(Path.GetTempPath(), "ch16-coding-" + Guid.NewGuid().ToString("N")[..8]);
        string root = Path.Combine(work, "repo");
        SampleRepo.Create(root);
        File.WriteAllText(Path.Combine(work, "outside.txt"), "not yours");
        ScratchScope scope = new(root);
        try
        {
            // 1-2. The scope itself.
            Check("src/Discount.cs resolves inside the scratch repo",
                scope.TryResolve("src/Discount.cs", out string full, out _) && full.StartsWith(scope.Root));
            string[] hostile = ["../outside.txt", "src/../../outside.txt", Path.Combine(work, "outside.txt"), ".git/config", "src/.GIT/x", " ",
                                ".git::$INDEX_ALLOCATION/config", ".git./config", "README.md:x"];
            Check("refused: ../outside.txt, src/../../outside.txt, an absolute path, .git/config, src/.GIT/x, a blank path",
                hostile[..6].All(p => !scope.TryResolve(p, out _, out _)));
            Check("refused: .git::$INDEX_ALLOCATION/config, .git./config and README.md:x (NTFS stream and trailing-dot names)",
                hostile[6..].All(p => !scope.TryResolve(p, out _, out _)));

            // 3. Links. A symbolic link needs a privilege on Windows; a junction does not.
            string link = Path.Combine(root, "escape");
            string kind = "symbolic link";
            try { Directory.CreateSymbolicLink(link, work); }
            catch (Exception ex) when (OperatingSystem.IsWindows() && ex is IOException or UnauthorizedAccessException)
            {
                kind = "junction";
                Shell("cmd", "/c", "mklink", "/J", link, work);
            }
            Check($"refused: escape/outside.txt through a {kind} inside the repo",
                Directory.Exists(link) && !scope.TryResolve("escape/outside.txt", out _, out string why) && why.Contains("link"));
            Directory.Delete(link);

            // 3b. A hard link is not a reparse point: the write must replace the name, not write through it.
            string hardLink = Path.Combine(root, "src", "hl.txt");
            if (OperatingSystem.IsWindows()) Shell("cmd", "/c", "mklink", "/H", hardLink, Path.Combine(work, "outside.txt"));
            else Shell("ln", Path.Combine(work, "outside.txt"), hardLink);
            string wrote = new CodingTools(scope).WriteFile("src/hl.txt", "changed by agent");
            Check("a write to src/hl.txt, a hard link to a file outside the repo, replaces the name; the file outside is untouched",
                wrote.StartsWith("Wrote") && File.ReadAllText(hardLink) == "changed by agent" &&
                File.ReadAllText(Path.Combine(work, "outside.txt")) == "not yours");
            File.Delete(hardLink);

            // 4. Reads run at once.
            (ChatClientAgent agent, AgentSession session, AgentResponse read, ScriptedToolChatClient client) =
                await RunAsync(scope, ("read_file", new() { ["path"] = "src/Discount.cs" }));
            Check("read_file runs with no approval request and returns the file",
                !Requests(read).Any() && client.Turns[^1].Results.Any(r => r.Result?.ToString()?.Contains("price * percent") == true));

            (_, _, AgentResponse listed, client) = await RunAsync(scope, ("list_files", new()));
            Check("list_files shows README.md and src/Discount.cs, nothing under .git",
                client.Turns[^1].Results.Single().Result?.ToString() == "README.md\nsrc/Discount.cs");

            // 5-6. Writes wait for a human.
            Dictionary<string, object?> write = new() { ["path"] = "src/Discount.cs", ["content"] = "// rewritten\n" };
            (agent, session, AgentResponse paused, _) = await RunAsync(scope, ("write_file", write));
            ToolApprovalRequestContent? request = Requests(paused).SingleOrDefault();
            Check("write_file comes back as a ToolApprovalRequestContent and the file is unchanged",
                request is not null && File.ReadAllText(Path.Combine(root, "src", "Discount.cs")) == SampleRepo.Discount);

            await agent.RunAsync([new ChatMessage(ChatRole.User, [request!.CreateResponse(approved: false, reason: "no")])], session);
            Check("rejected: the file is still unchanged", File.ReadAllText(Path.Combine(root, "src", "Discount.cs")) == SampleRepo.Discount);

            (agent, session, paused, _) = await RunAsync(scope, ("write_file", write));
            await agent.RunAsync([new ChatMessage(ChatRole.User, [Requests(paused).Single().CreateResponse(approved: true)])], session);
            Check("approved: the write lands in the scratch repo",
                File.ReadAllText(Path.Combine(root, "src", "Discount.cs")) == "// rewritten\n");

            // 7. Approval does not widen the scope.
            Dictionary<string, object?> escape = new() { ["path"] = "../outside.txt", ["content"] = "overwritten" };
            (agent, session, paused, client) = await RunAsync(scope, ("write_file", escape));
            await agent.RunAsync([new ChatMessage(ChatRole.User, [Requests(paused).Single().CreateResponse(approved: true)])], session);
            Check("an approved write to ../outside.txt is still refused by the scope; the file outside is untouched",
                File.ReadAllText(Path.Combine(work, "outside.txt")) == "not yours" &&
                client.Turns[^1].Results.Any(r => r.Result?.ToString()?.StartsWith("Refused") == true));
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }

        Console.WriteLine($"\nRESULT {passed}/{total}");
        return passed == total ? 0 : 1;
    }

    private static void Shell(string file, params string[] args)
    {
        System.Diagnostics.ProcessStartInfo psi = new(file) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"{file} {string.Join(' ', args)} failed: {p.StandardError.ReadToEnd()}");
    }

    private static IEnumerable<ToolApprovalRequestContent> Requests(AgentResponse r) =>
        r.Messages.SelectMany(m => m.Contents).OfType<ToolApprovalRequestContent>();

    private static async Task<(ChatClientAgent, AgentSession, AgentResponse, ScriptedToolChatClient)> RunAsync(
        ScratchScope scope, (string Tool, Dictionary<string, object?> Args) step)
    {
        ScriptedToolChatClient client = null!;
        client = new(turn => turn.Results.Count == 0 ? client.Call(step.Tool, step.Args) : ScriptedToolChatClient.Say("done"));
        ChatClientAgent agent = CodingTools.CreateAgent(client, scope);
        AgentSession session = await agent.CreateSessionAsync();
        return (agent, session, await agent.RunAsync("Work on the repo.", session), client);
    }
}
