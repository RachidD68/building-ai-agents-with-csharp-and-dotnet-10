// ============================================================================
// Chapter 17 — Agent Skills: file-based, inline, and class-based
// ----------------------------------------------------------------------------
// One SkillsAgent surfaces three different skill styles through a single
// AgentSkillsProvider:
//
//   1. File-based — skills/expense-report/ on disk with a SKILL.md and a
//      references/POLICY_FAQ.md the agent reads on demand.
//   2. Inline — an AgentInlineSkill named "code-style" with one static
//      resource that lists the team's coding conventions.
//   3. Class-based — UnitConverterSkill : AgentClassSkill<...> with a
//      [AgentSkillResource] conversion table and a [AgentSkillScript] that
//      performs the multiplication in-process.
//
// AgentSkillsProviderBuilder mixes the three sources into one provider,
// wires it through ChatClientAgentOptions.AIContextProviders, and lets MAF
// inject the load_skill / read_skill_resource / run_skill_script tools
// automatically. The streaming loop prints every one of those tool calls so
// you can watch progressive disclosure happen. Run with Ollama (default) or
// any configured provider.
// ============================================================================

using System.Diagnostics;
using System.Text.Json;
using Ch17.AgentSkills;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgenticAI.Common.Extensions;

HostApplicationBuilder hostBuilder = Host.CreateApplicationBuilder(args);
hostBuilder.Services.AddLlmChatClient(hostBuilder.Configuration);

using IHost host = hostBuilder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine("=== Chapter 17: Agent Skills demo ===");
Console.WriteLine();

// ── 1. File-based skill ────────────────────────────────────────────
// AgentSkillsProvider walks the skills/ directory, treats every
// subdirectory containing a SKILL.md as a skill, and surfaces it through
// load_skill / read_skill_resource. The on-disk skill (skills/expense-
// report/) ships with the project; the csproj copies the whole tree to
// the bin output on build.
string skillsRoot = Path.Combine(AppContext.BaseDirectory, "skills");

// ── 2. Inline skill — defined in code ──────────────────────────────
AgentInlineSkill codeStyleSkill = new AgentInlineSkill(
        name: "code-style",
        description: "Coding style guidelines and conventions for the Contoso engineering team.",
        instructions: """
            Use this skill when answering questions about coding style, naming,
            or layout conventions for Contoso services.
            1. Read the style-guide resource for the full set of rules.
            2. Answer based on those rules, quoting the relevant guideline.
            """)
    .AddResource(
        "style-guide",
        """
        # Contoso engineering style guide

        - C#: file-scoped namespaces, PascalCase types, camelCase locals,
          underscore-prefix private fields.
        - Async methods end with `Async`; cancellation tokens are NOT optional.
        - Maximum line length: 120 characters.
        - Records are preferred for DTOs; classes for entities with behaviour.
        - One public type per file. Internal helpers may live in the same file
          as their primary consumer.
        - All public APIs require XML documentation comments.
        """);

// ── 3. Class-based skill — UnitConverterSkill ──────────────────────
UnitConverterSkill unitConverterSkill = new();

// ── Mix sources with AgentSkillsProviderBuilder ────────────────────
// Builder gives us file + inline + class skills in one provider, plus
// optional filtering. UseFilter excludes any disk skill not on an
// allow-list — useful when a shared skills folder contains experimental
// skills you do not want to surface in this particular service.
//
// Since MAF 1.13 a file-based source MUST be paired with a script runner
// (the builder throws from Build() otherwise, even when no skill ships a
// script). We hand it RunFileScriptAsync below, a minimal subprocess
// runner. The on-disk expense-report skill bundles scripts/validate.py, and
// the demo's last question drives it end to end — advertise, load_skill,
// read_skill_resource (the policy FAQ), then run_skill_script through this
// exact runner.
HashSet<string> approvedSkillNames = new() { "expense-report", "code-style", "unit-converter" };

AgentSkillsProvider skillsProvider = new AgentSkillsProviderBuilder()
    .UseFileSkill(skillsRoot)
    .UseSkill(codeStyleSkill)
    .UseSkill(unitConverterSkill)
    .UseFileScriptRunner(RunFileScriptAsync)
    .UseFilter((skill, ctx) => approvedSkillNames.Contains(skill.Frontmatter.Name))
    .Build();

// ── Wire the agent ────────────────────────────────────────────────
ChatClientAgentOptions options = new()
{
    Name = "SkillsAgent",
    Description = "An assistant with file, inline, and class-based skills.",
    AIContextProviders = [skillsProvider],
    ChatOptions = new ChatOptions
    {
        Instructions = """
            You are the Contoso multi-skill assistant. You have access to several
            skills. Follow this order on every request:
            1. Pick the skill whose description matches the request.
            2. Call load_skill with that skill's name to read its full
               instructions BEFORE doing anything else.
            3. Follow the loaded instructions: call read_skill_resource for any
               resource they name, and call run_skill_script (using the exact
               script name the skill declares) when they tell you to run one.
            4. Only then answer, basing the answer on what the tools returned.
            Do not guess script or resource names, and do not do a skill's work
            yourself when it provides a script. If no skill fits, answer from
            general knowledge and say so honestly.
            """
    },
};

ChatClientAgent skillsAgent = new(chatClient, options: options);

// The skills provider gates ITS OWN injected tools behind approval. Without a
// rule, every one of them pauses the run — including the read-only load_skill
// and read_skill_resource, which turns progressive disclosure into a stream of
// prompts. ReadOnlyToolsAutoApprovalRule waves those through and leaves only
// run_skill_script (the one that executes code) for a human.
ToolApprovalAgent agent = new(skillsAgent, new ToolApprovalAgentOptions
{
    AutoApprovalRules = [AgentSkillsProvider.ReadOnlyToolsAutoApprovalRule],
});

AgentSession session = await agent.CreateSessionAsync(cts.Token);

// ── Ask the agent questions that hit each of the three skills ─────
// The expense-report question exercises the full progressive-disclosure
// lifecycle in one turn — advertise, load_skill, read_skill_resource
// (the policy FAQ), run_skill_script (the validate.py subprocess), then the
// final answer. The other two show the load → read path on the inline and
// class-based skills.
string[] questions =
[
    "Is a $90 client dinner on a domestic trip an allowed expense?",            // expense-report (file) skill
    "What's our team's rule on async method names in C#?",                      // code-style (inline) skill
    "Convert 12.4 miles to kilometres.",                                        // unit-converter (class) skill
];

foreach (string question in questions)
{
    Console.WriteLine($">> {question}");

    // run_skill_script sits behind ReadOnlyToolsAutoApprovalRule, so the run
    // can PAUSE with a ToolApprovalRequestContent. Since MAF 1.14, leaving
    // that request unanswered and continuing the session throws — approvals
    // bind to the request id. Collect the requests, answer them, resume.
    List<ToolApprovalRequestContent> approvals = [];

    async Task DrainAsync(IAsyncEnumerable<AgentResponseUpdate> updates)
    {
        await foreach (AgentResponseUpdate update in updates)
        {
            foreach (AIContent content in update.Contents)
            {
                switch (content)
                {
                    // The framework-injected skill tools surface as ordinary
                    // function calls. Printing them is what turns "progressive
                    // disclosure" from a diagram into something you can watch.
                    case FunctionCallContent call:
                        Console.WriteLine($"   [tool] {call.Name}({FormatArgs(call.Arguments)})");
                        break;

                    case FunctionResultContent result:
                        Console.WriteLine($"   [ ->  ] {Truncate(result.Result?.ToString())}");
                        break;

                    case ToolApprovalRequestContent request:
                        approvals.Add(request);
                        Console.WriteLine(
                            $"   [approval requested] {(request.ToolCall as FunctionCallContent)?.Name}");
                        break;

                    case TextContent { Text: { Length: > 0 } text }:
                        Console.Write(text);
                        break;
                }
            }
        }
    }

    await DrainAsync(agent.RunStreamingAsync(question, session, cancellationToken: cts.Token));

    if (approvals.Count > 0)
    {
        List<AIContent> decisions = [];
        foreach (ToolApprovalRequestContent request in approvals)
        {
            Console.WriteLine(
                $"   [approval] {(request.ToolCall as FunctionCallContent)?.Name} -> APPROVE (demo auto-approves its own bundled script)");
            decisions.Add(request.CreateResponse(
                approved: true, reason: "Reviewed: bundled demo script."));
        }
        approvals.Clear();

        ChatMessage decisionMessage = new(ChatRole.User, decisions);
        await DrainAsync(agent.RunStreamingAsync(
            [decisionMessage], session, cancellationToken: cts.Token));
    }

    Console.WriteLine();
    Console.WriteLine();
}

Console.WriteLine("=== Done ===");

// ── Helpers ────────────────────────────────────────────────────────

// Compact one-line rendering of a tool call's arguments for the transcript.
static string FormatArgs(IDictionary<string, object?>? args)
{
    if (args is null || args.Count == 0) return "";
    return string.Join(", ", args.Select(kv => $"{kv.Key}={kv.Value}"));
}

// Tool results (a loaded SKILL.md body, a resource, a script's stdout) can be
// long. Keep the transcript readable.
static string Truncate(string? value, int max = 96)
{
    value = value?.Replace("\r", " ").Replace("\n", " ").Trim() ?? "";
    return value.Length <= max ? value : value[..max] + "…";
}

// Minimal subprocess runner for file-based skill scripts (MAF 1.16 shape).
// The runner delegate receives the owning skill, the AgentFileSkillScript
// (with its absolute FullPath), and the raw JSON arguments — a string array
// of command-line arguments, per AgentFileSkillScript.ParametersSchema. It
// picks an interpreter from the file extension, runs the script, and returns
// stdout. This is a demonstration; production runners belong in a sandbox.
static async Task<object?> RunFileScriptAsync(
    AgentFileSkill skill,
    AgentFileSkillScript script,
    JsonElement? arguments,
    IServiceProvider? services,
    CancellationToken cancellationToken)
{
    string interpreter = Path.GetExtension(script.FullPath).ToLowerInvariant() switch
    {
        ".py" => "python",
        ".js" => "node",
        ".ps1" => "powershell",
        _ => throw new NotSupportedException(
            $"No interpreter registered for {script.FullPath}."),
    };

    ProcessStartInfo psi = new(interpreter)
    {
        RedirectStandardOutput = true,
        UseShellExecute = false,
    };
    psi.ArgumentList.Add(script.FullPath);

    // The script's schema declares its arguments as a JSON array of strings,
    // and each becomes a command-line argument. Be tolerant of how the model
    // serialises that array: some providers send a real JSON array, others
    // (several local Ollama models among them) send the array as a JSON
    // string. ParseCliArguments normalises both shapes.
    foreach (string arg in ParseCliArguments(arguments))
    {
        psi.ArgumentList.Add(arg);
    }

    using Process process = Process.Start(psi)
        ?? throw new InvalidOperationException($"Could not start '{interpreter}'.");

    // A hung script must not hang the agent run: give the subprocess a
    // wall-clock budget on top of the caller's token, and kill it on expiry.
    using CancellationTokenSource timeout =
        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeout.CancelAfter(TimeSpan.FromSeconds(30));

    string output;
    try
    {
        output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
        try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
        return "Error: script timed out after 30 seconds and was terminated.";
    }

    // Silent failure must not read as success — a crashing script returns a
    // model-readable error instead of whatever partial stdout it produced.
    if (process.ExitCode != 0)
    {
        string detail = output.Trim();
        return $"Error: script exited with code {process.ExitCode}."
             + (detail.Length > 0 ? $" Output: {detail}" : "");
    }

    return output.Trim();
}

// Turn the raw JSON the agent supplied into an ordered list of CLI arguments,
// accepting either a JSON array (["meals", "90"]) or that same array sent as a
// JSON string ("[\"meals\", \"90\"]").
static IReadOnlyList<string> ParseCliArguments(JsonElement? arguments)
{
    if (arguments is not { } element)
    {
        return [];
    }

    // A model may stringify the array; if so, parse the inner JSON.
    if (element.ValueKind == JsonValueKind.String)
    {
        string raw = element.GetString() ?? "";
        try
        {
            // Clone() detaches the element from the document, so the document
            // itself can (and must) be disposed here.
            using JsonDocument doc = JsonDocument.Parse(raw);
            element = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            // Not nested JSON — treat the whole string as a single argument.
            return [raw];
        }
    }

    return element.ValueKind == JsonValueKind.Array
        ? [.. element.EnumerateArray().Select(item =>
            item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : item.GetRawText())]
        : [];
}
