using System.Security.Cryptography;
using System.Text;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;

namespace Ch15.DurableReview;

/// <summary>
/// The graph's version: a hash of its executors (id and type), edges and ports,
/// plus the rules version. MAF refuses a checkpoint whose graph changed shape;
/// it cannot see a rule that changed inside an executor, so the rules version
/// is part of what the application stores and compares.
/// </summary>
public static class GraphVersion
{
    public static string Of(Workflow workflow, string rulesVersion)
    {
        List<string> lines = [$"start {workflow.StartExecutorId}"];
        lines.AddRange(workflow.ReflectExecutors().Select(e => $"executor {e.Key} {e.Value.ExecutorType?.FullName}"));
        foreach ((string source, HashSet<EdgeInfo> edges) in workflow.ReflectEdges())
            lines.AddRange(edges.Select(e =>
                $"edge {source} {e.Kind} {string.Join(',', e.Connection.SourceIds)} -> {string.Join(',', e.Connection.SinkIds)}" +
                (e is DirectEdgeInfo { HasCondition: true } ? " (conditional)" : "")));
        lines.AddRange(workflow.ReflectPorts().Select(p => $"port {p.Key} {p.Value.RequestType} -> {p.Value.ResponseType}"));
        lines.Sort(StringComparer.Ordinal);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines)));
        return $"{rulesVersion}/{Convert.ToHexString(hash)[..12].ToLowerInvariant()}";
    }
}
