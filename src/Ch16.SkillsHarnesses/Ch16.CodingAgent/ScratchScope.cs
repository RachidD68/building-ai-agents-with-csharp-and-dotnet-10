namespace Ch16.CodingAgent;

/// <summary>
/// The only folder the coding agent may touch. Every path the model sends is resolved
/// here: relative only, inside the root after normalization, never under .git, and
/// never through a symbolic link or junction. A scope is not a sandbox: it limits what
/// the agent's tools do, not what the process could do.
/// </summary>
public sealed class ScratchScope(string root)
{
    public string Root { get; } = Path.GetFullPath(root);

    public bool TryResolve(string? relative, out string fullPath, out string refusal)
    {
        fullPath = "";
        refusal = "";
        if (string.IsNullOrWhiteSpace(relative)) { refusal = "empty path"; return false; }
        if (Path.IsPathRooted(relative)) { refusal = "absolute paths are outside the scratch repo"; return false; }

        string candidate = Path.GetFullPath(Path.Combine(Root, relative));
        string rel = Path.GetRelativePath(Root, candidate);
        if (rel == ".." || rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(rel))
        {
            refusal = "the path leaves the scratch repo"; return false;
        }
        string[] parts = rel.Split(Path.DirectorySeparatorChar);
        if (parts.Any(p => p.Equals(".git", StringComparison.OrdinalIgnoreCase)))
        {
            refusal = ".git is off limits"; return false;
        }
        // A link inside the root can point anywhere, so refuse any link on the way down.
        string walk = Root;
        foreach (string part in parts)
        {
            walk = Path.Combine(walk, part);
            if ((File.Exists(walk) || Directory.Exists(walk)) &&
                File.GetAttributes(walk).HasFlag(FileAttributes.ReparsePoint))
            {
                refusal = "the path goes through a link"; return false;
            }
        }
        fullPath = candidate;
        return true;
    }
}
