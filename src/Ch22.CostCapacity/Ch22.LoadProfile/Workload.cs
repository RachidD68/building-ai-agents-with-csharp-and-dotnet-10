using System.Text.Json;

namespace Ch22.LoadProfile;

/// <summary>
/// A versioned workload file: the tasks, the concurrency steps, the service
/// shape (model slots, bounded queue) and the limit, all declared before the
/// run. A number measured on another version of this file is another number.
/// </summary>
public sealed record Workload(
    string Version,
    string System,
    int MaxOutputTokens,
    IReadOnlyList<WorkTask> Tasks,
    IReadOnlyList<int> Steps,
    int RequestsPerStep,
    ServiceShape Service,
    int ToolDelayMs,
    int TimeoutMs,
    DeclaredLimit Limit)
{
    static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    public static Workload Load(string path)
    {
        Workload w = JsonSerializer.Deserialize<Workload>(File.ReadAllText(path), s_json)
            ?? throw new InvalidDataException($"{path}: empty workload.");
        if (string.IsNullOrWhiteSpace(w.Version)) throw new InvalidDataException($"{path}: no version.");
        if (w.Tasks is not { Count: > 0 } || w.Steps is not { Count: > 0 } || w.RequestsPerStep < 1)
            throw new InvalidDataException($"{w.Version}: tasks, steps and requestsPerStep are required.");
        if (w.Service is not { Slots: > 0, QueueCapacity: >= 0 }) throw new InvalidDataException($"{w.Version}: bad service shape.");
        return w;
    }
}

/// <param name="Expected">The label an accepted answer contains.</param>
public sealed record WorkTask(string Id, string Prompt, string Expected);

/// <param name="Slots">Model calls in flight at once.</param>
/// <param name="QueueCapacity">Requests allowed to wait for a slot; the next one gets a busy response.</param>
/// <param name="BusyRetryAfterMs">How long a client waits after a busy response before its next request.</param>
public sealed record ServiceShape(int Slots, int QueueCapacity, int BusyRetryAfterMs);

/// <summary>The limit, declared before the run. A step crosses it when any line fails.</summary>
public sealed record DeclaredLimit(double P95TotalMs, double MaxErrorRate, double MaxBusyRate, double MinAcceptedRate);
