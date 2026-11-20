using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Ch22.CostModel;

/// <summary>
/// What one workload consumed: every model call with the UsageDetails the
/// connector reported (null when it reported none), the tools it ran, and the
/// outcome of every task. Runtime hours carry the cost a token count misses.
/// </summary>
public sealed record UsageLog(
    string Workload,
    DateOnly Date,
    IReadOnlyList<CallRecord> Calls,
    IReadOnlyList<TaskRecord> Tasks,
    IReadOnlyDictionary<string, decimal>? RuntimeHours = null)
{
    static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static UsageLog Load(string path) =>
        JsonSerializer.Deserialize<UsageLog>(File.ReadAllText(path), s_json)
        ?? throw new InvalidDataException($"{path}: empty usage log.");

    public string ToJson() => JsonSerializer.Serialize(this, s_json);
}

/// <param name="Status">ok, throttled, timeout or error: a failed call can still be billed.</param>
public sealed record CallRecord(string Task, string Model, int Attempt, UsageDetails? Usage,
    IReadOnlyList<string>? Tools = null, string Status = "ok");

/// <param name="Outcome">accepted, rejected or abandoned. Only accepted counts as delivered.</param>
public sealed record TaskRecord(string Id, string Outcome)
{
    public bool Accepted => Outcome == "accepted";
}
