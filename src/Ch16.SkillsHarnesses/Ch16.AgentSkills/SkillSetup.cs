using System.Diagnostics;
using System.Text.Json;
using Microsoft.Agents.AI;

namespace Ch16.AgentSkills;

/// <summary>The three skill sources the demo mixes, and the subprocess runner file skills need.</summary>
public static class SkillSetup
{
    public static readonly string[] Approved = ["expense-report", "code-style", "unit-converter"];

    /// <summary>An inline skill: instructions and one resource, defined in code.</summary>
    public static AgentInlineSkill CodeStyle() => new AgentInlineSkill(
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
            - One public type per file.
            - All public APIs require XML documentation comments.
            """);

    /// <summary>
    /// File + inline + class skills in one provider. A file source needs a script
    /// runner (Build() throws without one); the filter is an allow-list.
    /// </summary>
    public static AgentSkillsProvider Build(string skillsRoot, IReadOnlyCollection<string> allowed,
        AgentFileSkillScriptRunner? runner = null) =>
        new AgentSkillsProviderBuilder()
            .UseFileSkill(skillsRoot)
            .UseSkill(CodeStyle())
            .UseSkill(new UnitConverterSkill())
            .UseFileScriptRunner(runner ?? RunFileScriptAsync)
            .UseFilter((skill, _) => allowed.Contains(skill.Frontmatter.Name))
            .Build();

    /// <summary>
    /// Minimal subprocess runner for file-skill scripts: picks an interpreter by
    /// extension, passes the arguments, returns stdout, and kills the process after
    /// 30 seconds. A demonstration; production runners belong in a sandbox.
    /// </summary>
    public static async Task<object?> RunFileScriptAsync(
        AgentFileSkill skill, AgentFileSkillScript script, JsonElement? arguments,
        IServiceProvider? services, CancellationToken cancellationToken)
    {
        string interpreter = Path.GetExtension(script.FullPath).ToLowerInvariant() switch
        {
            ".py" => "python",
            ".js" => "node",
            ".ps1" => "pwsh",
            _ => throw new NotSupportedException($"No interpreter registered for {script.FullPath}."),
        };

        ProcessStartInfo psi = new(interpreter) { RedirectStandardOutput = true, UseShellExecute = false };
        psi.ArgumentList.Add(script.FullPath);
        foreach (string arg in ParseCliArguments(arguments)) psi.ArgumentList.Add(arg);

        using Process process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Could not start '{interpreter}'.");
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        string output;
        try
        {
            output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already gone */ }
            return "Error: script timed out after 30 seconds and was terminated.";
        }

        // A crashing script returns a model-readable error, never partial stdout as success.
        return process.ExitCode == 0
            ? output.Trim()
            : $"Error: script exited with code {process.ExitCode}." + (output.Trim().Length > 0 ? $" Output: {output.Trim()}" : "");
    }

    /// <summary>
    /// The script schema asks for a JSON array of strings. Some local models send
    /// the array as a JSON string instead; accept both shapes.
    /// </summary>
    public static IReadOnlyList<string> ParseCliArguments(JsonElement? arguments)
    {
        if (arguments is not { } element) return [];
        if (element.ValueKind == JsonValueKind.String)
        {
            string raw = element.GetString() ?? "";
            try
            {
                using JsonDocument doc = JsonDocument.Parse(raw);
                element = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                return [raw];   // not nested JSON: one argument
            }
        }
        return element.ValueKind == JsonValueKind.Array
            ? [.. element.EnumerateArray().Select(item =>
                item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : item.GetRawText())]
            : [];
    }
}
