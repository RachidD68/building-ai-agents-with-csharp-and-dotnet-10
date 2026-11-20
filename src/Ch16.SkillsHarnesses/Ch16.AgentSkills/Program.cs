// ============================================================================
// Agent Skills: file-based, inline, and class-based
// ----------------------------------------------------------------------------
// One agent surfaces three skill styles through a single AgentSkillsProvider:
//
//   1. File-based - skills/expense-report/ on disk: a SKILL.md, a
//      references/POLICY_FAQ.md and a scripts/validate.py.
//   2. Inline - an AgentInlineSkill "code-style" with one resource.
//   3. Class-based - UnitConverterSkill : AgentClassSkill<T> with an
//      [AgentSkillResource] table and an [AgentSkillScript] that runs in-process.
//
// AgentSkillsProviderBuilder mixes the three (SkillSetup.Build) and the
// provider sends the model three tools: load_skill, read_skill_resource and
// run_skill_script. All three require approval; ReadOnlyToolsAutoApprovalRule
// lets the two read tools through and leaves run_skill_script for a human.
//
//   dotnet run                  live run; the demo approves its own bundled scripts
//   dotnet run -- --self-test   offline checks with a scripted client
// ============================================================================

using System.Text.Json;
using AgenticAI.Common.Extensions;
using Ch16.AgentSkills;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

if (args.Contains("--self-test")) return await SelfTest.RunAsync();

HostApplicationBuilder hostBuilder = Host.CreateApplicationBuilder(args);
hostBuilder.Services.AddLlmChatClient(hostBuilder.Configuration);

using IHost host = hostBuilder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine("=== Agent Skills demo ===");
Console.WriteLine();

// The csproj copies skills/ next to the binary.
AgentSkillsProvider skillsProvider =
    SkillSetup.Build(Path.Combine(AppContext.BaseDirectory, "skills"), SkillSetup.Approved);

ChatClientAgent skillsAgent = new(chatClient, new ChatClientAgentOptions
{
    Name = "SkillsAgent",
    Description = "An assistant with file, inline, and class-based skills.",
    AIContextProviders = [skillsProvider],
    ChatOptions = new ChatOptions
    {
        Instructions = """
            You are the Contoso multi-skill assistant. Follow this order on every request:
            1. Pick the skill whose description matches the request.
            2. Call load_skill with that skill's name BEFORE doing anything else.
            3. Follow the loaded instructions: call read_skill_resource for any
               resource they name, and call run_skill_script (using the exact
               script name the skill declares) when they tell you to run one.
            4. Only then answer, based on what the tools returned.
            Do not guess script or resource names. If no skill fits, say so.
            """,
    },
});

// Read tools flow; run_skill_script pauses for a human.
ToolApprovalAgent agent = new(skillsAgent, new ToolApprovalAgentOptions
{
    AutoApprovalRules = [AgentSkillsProvider.ReadOnlyToolsAutoApprovalRule],
});

AgentSession session = await agent.CreateSessionAsync(cts.Token);

string[] questions =
[
    "Is a $90 client dinner on a domestic trip an allowed expense?",   // file skill
    "What's our team's rule on async method names in C#?",             // inline skill
    "Convert 12.4 miles to kilometres.",                               // class skill
];

foreach (string question in questions)
{
    Console.WriteLine($">> {question}");
    List<ToolApprovalRequestContent> approvals = [];
    await DrainAsync(agent.RunStreamingAsync(question, session, cancellationToken: cts.Token), approvals);

    if (approvals.Count > 0)
    {
        // Approvals bind to the request id (see the authorized-actions chapter).
        List<AIContent> decisions = [];
        foreach (ToolApprovalRequestContent request in approvals)
        {
            Console.WriteLine($"   [approval] {(request.ToolCall as FunctionCallContent)?.Name} -> APPROVE (bundled demo script)");
            decisions.Add(request.CreateResponse(approved: true, reason: "Reviewed: bundled demo script."));
        }
        await DrainAsync(agent.RunStreamingAsync([new ChatMessage(ChatRole.User, decisions)], session,
            cancellationToken: cts.Token), []);
    }
    Console.WriteLine();
    Console.WriteLine();
}

Console.WriteLine("=== Done ===");
return 0;

// Prints every skill tool call, so progressive disclosure can be watched.
static async Task DrainAsync(IAsyncEnumerable<AgentResponseUpdate> updates, List<ToolApprovalRequestContent> approvals)
{
    await foreach (AgentResponseUpdate update in updates)
    {
        foreach (AIContent content in update.Contents)
        {
            switch (content)
            {
                case FunctionCallContent call:
                    Console.WriteLine($"   [tool] {call.Name}({string.Join(", ", (call.Arguments ?? new Dictionary<string, object?>()).Select(kv => $"{kv.Key}={kv.Value}"))})");
                    break;
                case FunctionResultContent result:
                    Console.WriteLine($"   [ ->  ] {Truncate(result.Result?.ToString())}");
                    break;
                case ToolApprovalRequestContent request:
                    approvals.Add(request);
                    Console.WriteLine($"   [approval requested] {(request.ToolCall as FunctionCallContent)?.Name}");
                    break;
                case TextContent { Text: { Length: > 0 } text }:
                    Console.Write(text);
                    break;
            }
        }
    }
}

static string Truncate(string? value, int max = 96)
{
    value = value?.Replace("\r", " ").Replace("\n", " ").Trim() ?? "";
    return value.Length <= max ? value : value[..max] + "…";
}
