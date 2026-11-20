using System.Text.Json;
using AgenticAI.Testing;
using Ch16.AgentSkills;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

/// <summary>
/// Offline checks: dotnet run -- --self-test. A scripted client calls the skill tools;
/// no model, no Python (file scripts are refused or counted, never run).
/// </summary>
static class SelfTest
{
    public static async Task<int> RunAsync()
    {
        Console.WriteLine("=== Agent Skills self-test (offline) ===\n");
        int passed = 0, total = 0;
        void Check(string name, bool ok)
        {
            total++; passed += ok ? 1 : 0;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
        }

        string skillsRoot = Path.Combine(AppContext.BaseDirectory, "skills");
        int fileScriptRuns = 0;
        AgentFileSkillScriptRunner counting = (_, _, _, _, _) => { Interlocked.Increment(ref fileScriptRuns); return Task.FromResult<object?>("ok"); };

        // 1. Advertising: names and descriptions only.
        (ScriptedToolChatClient advert, _) = await RunAsync(SkillSetup.Build(skillsRoot, SkillSetup.Approved, counting), []);
        string instructions = advert.Turns[0].Options?.Instructions ?? "";
        Check("the first request advertises all three skills and sends load_skill, read_skill_resource, run_skill_script",
            SkillSetup.Approved.All(instructions.Contains) &&
            advert.Turns[0].ToolNames.Order().SequenceEqual(["load_skill", "read_skill_resource", "run_skill_script"]));
        Check("advertising sends no skill body (\"# Expense report workflow\" is not in the first request)",
            !instructions.Contains("Expense report workflow"));

        // 2. Loading each source through the same tool.
        string[] bodies = new string[3];
        for (int i = 0; i < 3; i++)
        {
            (_, AgentResponse loaded) = await RunAsync(SkillSetup.Build(skillsRoot, SkillSetup.Approved, counting),
                [("load_skill", new() { ["skillName"] = SkillSetup.Approved[i] })]);
            bodies[i] = Result(loaded);
        }
        Check("load_skill returns the file, inline and class skill bodies with no approval prompt",
            bodies[0].Contains("Expense report workflow") && bodies[1].Contains("style-guide") && bodies[2].Contains("conversion-table"));

        (_, AgentResponse resource) = await RunAsync(SkillSetup.Build(skillsRoot, SkillSetup.Approved, counting),
            [("read_skill_resource", new() { ["skillName"] = "code-style", ["resourceName"] = "style-guide" })]);
        Check("read_skill_resource returns the inline style-guide resource with no approval prompt",
            Result(resource).Contains("end with `Async`") && !Requests(resource).Any());

        // 3. Scripts stop for a human.
        Dictionary<string, object?> convert = new()
        {
            ["skillName"] = "unit-converter", ["scriptName"] = "convert",
            ["arguments"] = JsonSerializer.SerializeToElement(new { value = 12.4, factor = 1.60934 }),
        };
        (ToolApprovalAgent agent, AgentSession session, AgentResponse paused) =
            await StartAsync(SkillSetup.Build(skillsRoot, SkillSetup.Approved, counting), [("run_skill_script", convert)]);
        ToolApprovalRequestContent? request = Requests(paused).SingleOrDefault();
        Check("run_skill_script pauses with a ToolApprovalRequestContent (ReadOnlyToolsAutoApprovalRule does not cover it)",
            request is not null);
        AgentResponse approved = await agent.RunAsync([new ChatMessage(ChatRole.User, [request!.CreateResponse(approved: true)])], session);
        Check("approved: the class skill's convert script runs in-process and returns 19.9558",
            Result(approved).Contains("19.9558"));

        Dictionary<string, object?> validate = new()
        {
            ["skillName"] = "expense-report", ["scriptName"] = "scripts/validate.py",
            ["arguments"] = JsonSerializer.SerializeToElement(new[] { "meals", "90" }),
        };
        (agent, session, paused) = await StartAsync(SkillSetup.Build(skillsRoot, SkillSetup.Approved, counting), [("run_skill_script", validate)]);
        await agent.RunAsync([new ChatMessage(ChatRole.User, [Requests(paused).Single().CreateResponse(approved: false, reason: "no")])], session);
        Check("rejected: the file script runner is never called", Volatile.Read(ref fileScriptRuns) == 0);
        (agent, session, paused) = await StartAsync(SkillSetup.Build(skillsRoot, SkillSetup.Approved, counting), [("run_skill_script", validate)]);
        await agent.RunAsync([new ChatMessage(ChatRole.User, [Requests(paused).Single().CreateResponse(approved: true)])], session);
        Check("the same file-script call, approved: the runner is called exactly once", Volatile.Read(ref fileScriptRuns) == 1);

        // 4. The allow-list filter.
        (_, AgentResponse filtered) = await RunAsync(SkillSetup.Build(skillsRoot, ["code-style", "unit-converter"], counting),
            [("load_skill", new() { ["skillName"] = "expense-report" })]);
        Check("UseFilter: a disk skill off the allow-list cannot be loaded",
            !Result(filtered).Contains("POLICY_FAQ"));

        // 5. Argument shapes the runner accepts.
        Check("ParseCliArguments accepts a JSON array and the same array sent as a string",
            SkillSetup.ParseCliArguments(JsonSerializer.SerializeToElement(new[] { "meals", "90" })).SequenceEqual(["meals", "90"]) &&
            SkillSetup.ParseCliArguments(JsonSerializer.SerializeToElement("[\"meals\", \"90\"]")).SequenceEqual(["meals", "90"]));

        Console.WriteLine($"\nRESULT {passed}/{total}");
        return passed == total ? 0 : 1;
    }

    private static string Result(AgentResponse r) =>
        string.Join("\n", r.Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(f => f.Result?.ToString()));

    private static IEnumerable<ToolApprovalRequestContent> Requests(AgentResponse r) =>
        r.Messages.SelectMany(m => m.Contents).OfType<ToolApprovalRequestContent>();

    private static async Task<(ScriptedToolChatClient, AgentResponse)> RunAsync(
        AgentSkillsProvider provider, (string Tool, Dictionary<string, object?> Args)[] plan)
    {
        (_, _, AgentResponse response, ScriptedToolChatClient client) = await StartCoreAsync(provider, plan);
        return (client, response);
    }

    private static async Task<(ToolApprovalAgent, AgentSession, AgentResponse)> StartAsync(
        AgentSkillsProvider provider, (string Tool, Dictionary<string, object?> Args)[] plan)
    {
        (ToolApprovalAgent a, AgentSession s, AgentResponse r, _) = await StartCoreAsync(provider, plan);
        return (a, s, r);
    }

    private static async Task<(ToolApprovalAgent, AgentSession, AgentResponse, ScriptedToolChatClient)> StartCoreAsync(
        AgentSkillsProvider provider, (string Tool, Dictionary<string, object?> Args)[] plan)
    {
        ScriptedToolChatClient client = null!;
        client = new(turn => turn.Results.Count < plan.Length
            ? client.Call(plan[turn.Results.Count].Tool, plan[turn.Results.Count].Args)
            : ScriptedToolChatClient.Say("done"));
        ToolApprovalAgent agent = new(new ChatClientAgent(client, new ChatClientAgentOptions { AIContextProviders = [provider] }),
            new ToolApprovalAgentOptions { AutoApprovalRules = [AgentSkillsProvider.ReadOnlyToolsAutoApprovalRule] });
        AgentSession session = await agent.CreateSessionAsync();
        return (agent, session, await agent.RunAsync("Use your skills.", session), client);
    }
}
