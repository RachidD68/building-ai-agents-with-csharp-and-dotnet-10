using System.ComponentModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Ch16.CodingAgent;

/// <summary>
/// Three tools over one scratch repo. Reads are free; write_file is an
/// ApprovalRequiredAIFunction, so every write stops for a human, and the scope
/// is checked again when the approved call runs.
/// </summary>
public sealed class CodingTools(ScratchScope scope)
{
    [Description("Lists the files in the scratch repo, as paths relative to its root.")]
    public string ListFiles() =>
        string.Join('\n', Directory.GetFiles(scope.Root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(scope.Root, f).Replace('\\', '/'))
            .Where(f => !f.StartsWith(".git/", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal));

    [Description("Reads one file of the scratch repo.")]
    public string ReadFile([Description("Path relative to the repo root, for example src/Discount.cs.")] string path) =>
        !scope.TryResolve(path, out string full, out string refusal) ? $"Refused: {refusal}."
        : File.Exists(full) ? File.ReadAllText(full)
        : $"No such file: {path}.";

    [Description("Writes a whole file in the scratch repo. A human approves every write.")]
    public string WriteFile(
        [Description("Path relative to the repo root.")] string path,
        [Description("The complete new content of the file.")] string content)
    {
        if (!scope.TryResolve(path, out string full, out string refusal)) return $"Refused: {refusal}.";
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        // Write a new file and rename it over the old name. Writing in place would follow a
        // hard link to wherever its other name lives; a rename replaces only this name.
        string temp = $"{full}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, full, overwrite: true);
        return $"Wrote {path} ({content.Length} characters).";
    }

    public IList<AITool> AsTools() =>
    [
        AIFunctionFactory.Create(ListFiles, "list_files"),
        AIFunctionFactory.Create(ReadFile, "read_file"),
        new ApprovalRequiredAIFunction(AIFunctionFactory.Create(WriteFile, "write_file")),
    ];

    /// <summary>The coding agent: reads run at once; a write comes back as a ToolApprovalRequestContent.</summary>
    public static ChatClientAgent CreateAgent(IChatClient chatClient, ScratchScope scope) =>
        chatClient.AsAIAgent(
                name: "CodingAgent",
                instructions: """
                    You change code in a scratch repo. List the files, read before you write,
                    and write whole files with write_file. Keep changes small. Say what you changed.
                    """,
                tools: new CodingTools(scope).AsTools());
}
