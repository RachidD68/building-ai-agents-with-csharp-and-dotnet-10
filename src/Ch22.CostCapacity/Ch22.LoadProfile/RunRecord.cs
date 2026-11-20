using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ch22.CostModel;

namespace Ch22.LoadProfile;

/// <summary>Prints the step table, the machine it ran on, and the files a run leaves behind.</summary>
public static class RunRecord
{
    public static string Header =>
        $"{"c",3} {"n",4} {"acc",4} {"rej",4} {"busy",4} {"err",4} {"queue p95",10} {"tool p95",9} " +
        $"{"ttft p50/p95",13} {"gen p95",8} {"total p50/p95",14} {"acc/s",6} {"maxQ",5}";

    public static string Row(StepResult s) =>
        $"{s.Concurrency,3} {s.N,4} {s.Count(Outcome.Accepted),4} {s.Count(Outcome.Rejected),4} {s.Count(Outcome.Busy),4} {s.Errors,4} " +
        $"{Percentile.Ms(s.P(0.95, t => t.Queue)),10} {Percentile.Ms(s.P(0.95, t => t.Tool)),9} " +
        $"{Percentile.Ms(s.P(0.50, t => t.Ttft)) + "/" + Percentile.Ms(s.P(0.95, t => t.Ttft)),13} " +
        $"{Percentile.Ms(s.P(0.95, t => t.Generation)),8} " +
        $"{Percentile.Ms(s.P(0.50, t => t.Total)) + "/" + Percentile.Ms(s.P(0.95, t => t.Total)),14} " +
        $"{s.AcceptedPerSecond,6:F2} {s.MaxQueueDepth,5}";

    /// <summary>The machine, as the process sees it. A capacity number without it means nothing.</summary>
    public static IEnumerable<string> Machine()
    {
        yield return $"os: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}), .NET {Environment.Version}";
        yield return $"cpu: {CpuName()}, {Environment.ProcessorCount} logical processors";
        yield return $"memory visible to the process: {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024 * 1024):F1} GiB";
    }

    static string CpuName()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
                    "ProcessorNameString", null) as string ?? "unknown";
            if (File.Exists("/proc/cpuinfo"))
                return File.ReadLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("model name"))?.Split(':', 2)[1].Trim() ?? "unknown";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return "unknown";
    }

    /// <summary>Ollama's version and whether the model is resident (and how much of it in VRAM).</summary>
    public static async Task<IEnumerable<string>> OllamaAsync(HttpClient http, string model)
    {
        try
        {
            JsonNode? version = JsonNode.Parse(await http.GetStringAsync("api/version"));
            JsonNode? ps = JsonNode.Parse(await http.GetStringAsync("api/ps"));
            JsonNode? loaded = ps?["models"]?.AsArray().FirstOrDefault(m => m?["name"]?.GetValue<string>().StartsWith(model) == true);
            string resident = loaded is null ? "not loaded"
                : $"loaded, size {loaded["size"]} bytes, in VRAM {loaded["size_vram"]} bytes";
            return [$"ollama {version?["version"]}, model {model}: {resident}"];
        }
        catch (HttpRequestException ex)
        {
            return [$"ollama: not reachable ({ex.Message})"];
        }
    }

    public static string StepsJson(Workload w, LoadReport report, IEnumerable<string> machine) =>
        new JsonObject
        {
            ["workload"] = w.Version,
            ["limit"] = JsonSerializer.SerializeToNode(w.Limit),
            ["machine"] = new JsonArray([.. machine.Select(m => JsonValue.Create(m))]),
            ["declared"] = report.Declare(w.Limit),
            ["steps"] = new JsonArray([.. report.Steps.Select(s => (JsonNode)new JsonObject
            {
                ["concurrency"] = s.Concurrency, ["n"] = s.N,
                ["accepted"] = s.Count(Outcome.Accepted), ["rejected"] = s.Count(Outcome.Rejected),
                ["busy"] = s.Count(Outcome.Busy), ["errors"] = s.Errors,
                ["p50TotalMs"] = s.P(0.50, t => t.Total), ["p95TotalMs"] = s.P(0.95, t => t.Total),
                ["p95QueueMs"] = s.P(0.95, t => t.Queue), ["p95TtftMs"] = s.P(0.95, t => t.Ttft),
                ["acceptedPerSecond"] = Math.Round(s.AcceptedPerSecond, 3),
                ["acceptedRate"] = Math.Round(s.Rate(s.Count(Outcome.Accepted)), 3),
                ["crosses"] = s.Crosses(w.Limit),
            })]),
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    /// <summary>The run as a usage log the cost model reads: measured usage, every outcome, runtime hours.</summary>
    public static UsageLog Usage(Workload w, string model, LoadReport report, TimeSpan wall) =>
        new($"{w.Version} live", DateOnly.FromDateTime(DateTime.Now),
            // A busy response never reached the model, so it is a task outcome, not a call.
            [.. report.Steps.SelectMany((s, i) => s.Timings.Select((t, j) => (t, j)).Where(x => x.t.Outcome != Outcome.Busy)
                .Select(x => new CallRecord($"s{i}-{x.j}-{x.t.TaskId}", model, 1, x.t.Usage,
                    Status: x.t.Outcome.ToString().ToLowerInvariant())))],
            [.. report.Steps.SelectMany((s, i) => s.Timings.Select((t, j) =>
                new TaskRecord($"s{i}-{j}-{t.TaskId}", t.Outcome.ToString().ToLowerInvariant())))],
            new Dictionary<string, decimal> { ["workstation"] = Math.Round((decimal)wall.TotalHours, 4) });
}
