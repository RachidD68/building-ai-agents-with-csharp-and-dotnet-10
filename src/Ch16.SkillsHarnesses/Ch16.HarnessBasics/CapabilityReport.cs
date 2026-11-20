using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Ch16.Harness;

/// <summary>One row of the capability report: on by default, authorized by these options, exercised by the run.</summary>
public sealed record Capability(string Name, bool OnByDefault, bool Authorized, int Advertised, int Called);

/// <summary>
/// Says what a harness agent can do. "Default" is what a bare HarnessAgentOptions
/// switches on, "authorized" is what these options leave on, and the two counts come
/// from what the model was actually sent and what it actually called.
/// </summary>
public static class CapabilityReport
{
    private sealed record Row(string Name, Func<HarnessAgentOptions, bool> IsOn, params string[] ToolPrefixes);

    private static readonly Row[] Rows =
    [
        new("hosted web search", o => !o.DisableWebSearch, "web_search"),
        new("to-do list", o => !o.DisableTodoProvider, "todos_"),
        new("agent modes", o => !o.DisableAgentModeProvider, "mode_"),
        new("skills", o => !o.DisableAgentSkillsProvider, "load_skill", "read_skill_resource", "run_skill_script"),
        new("file memory", o => !o.DisableFileMemory, "file_memory_"),
        // Reading these two options only; both are [Experimental("MAAI001")] in 1.28.
#pragma warning disable MAAI001
        new("file access", o => o.FileAccessStore is not null, "file_access_"),
        new("background agents", o => o.BackgroundAgents?.Any() == true, "background_agents_"),
#pragma warning restore MAAI001
        new("tool auto-approval", o => !o.DisableToolAutoApproval),
        new("OpenTelemetry", o => !o.DisableOpenTelemetry),
    ];

    public static IReadOnlyList<Capability> Build(
        HarnessAgentOptions options, IEnumerable<string> advertised, IEnumerable<string> called)
    {
        HarnessAgentOptions defaults = new();
        List<string> sent = [.. advertised.Distinct()];
        List<string> used = [.. called];
        List<Capability> report = [];
        foreach (Row row in Rows)
        {
            bool Mine(string tool) => row.ToolPrefixes.Any(p => tool.StartsWith(p, StringComparison.Ordinal));
            report.Add(new(row.Name, row.IsOn(defaults), row.IsOn(options), sent.Count(Mine), used.Count(Mine)));
        }
        foreach (AITool tool in options.ChatOptions?.Tools ?? [])
        {
            report.Add(new($"your tool: {tool.Name}", false, true,
                sent.Count(t => t == tool.Name), used.Count(t => t == tool.Name)));
        }
        // The rows are a list you extend. A tool no row claims (a context provider's, or a new
        // harness provider's after an upgrade) gets this row, so it can't go unreported.
        bool Known(string tool) => (options.ChatOptions?.Tools ?? []).Any(t => t.Name == tool) ||
            Rows.Any(r => r.ToolPrefixes.Any(p => tool.StartsWith(p, StringComparison.Ordinal)));
        if (sent.Any(t => !Known(t)) || used.Any(t => !Known(t)))
        {
            report.Add(new("unclassified tools", false, true, sent.Count(t => !Known(t)), used.Count(t => !Known(t))));
        }
        return report;
    }

    /// <summary>The tool names the model was sent, across every recorded call.</summary>
    public static IEnumerable<string> Advertised(IEnumerable<ChatOptions?> calls) =>
        calls.SelectMany(o => o?.Tools ?? []).Select(t => t.Name).Distinct();

    /// <summary>The tool calls the model made in these messages.</summary>
    public static IEnumerable<string> Called(IEnumerable<ChatMessage> messages) =>
        messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Select(c => c.Name);

    /// <summary>Instruction characters, tool count and schema characters of one request.</summary>
    public static (int Instructions, int Tools, int SchemaChars) Size(ChatOptions? options) =>
        (options?.Instructions?.Length ?? 0,
         options?.Tools?.Count ?? 0,
         (options?.Tools ?? []).OfType<AIFunction>()
             .Sum(f => f.Name.Length + f.Description.Length + f.JsonSchema.GetRawText().Length));

    public static void Print(IReadOnlyList<Capability> report, TextWriter output)
    {
        output.WriteLine($"  {"capability",-28} {"default",-8} {"authorized",-11} {"tools sent",-11} called");
        foreach (Capability c in report)
        {
            output.WriteLine($"  {c.Name,-28} {(c.OnByDefault ? "on" : "off"),-8} " +
                             $"{(c.Authorized ? "on" : "off"),-11} {c.Advertised,-11} {c.Called}");
        }
    }
}
