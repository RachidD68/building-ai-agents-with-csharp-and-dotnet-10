// ============================================================================
// Exercise 01: Metrics Collector (basic)
// ----------------------------------------------------------------------------
// A response cache inside the agent pipeline, measured with three instruments
// on one meter, all tagged with cache.name and nothing else:
//   agent.cache.hits        Counter<long>
//   agent.cache.misses      Counter<long>
//   agent.cache.hit_rate    ObservableGauge<double>, hits / (hits + misses)
// cache.name comes from a fixed list of three, so the series count is fixed.
//
// The first edition's solution exported one counter named hit_rate that
// counted hits only: with no miss count next to it, no dashboard can turn it
// into a rate. The self-test reproduces that before checking the fix.
//
//   dotnet run                    100 requests (10 distinct) to the configured model
//   dotnet run -- --self-test     offline: echo model, the real MeterProvider
// ============================================================================

using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using AgenticAI.Common.Extensions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;

if (args.Contains("--self-test")) return await SelfTest.RunAsync();

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddOpenTelemetry().WithMetrics(m => m.AddMeter(CacheMetrics.MeterName).AddConsoleExporter());
builder.Services.AddLlmChatClient(builder.Configuration);
using IHost host = builder.Build();
await host.StartAsync();   // installs the MeterListener; without it the counters record into the void

Console.WriteLine("=== Metrics collector: agent.cache.* ===\n");
using CacheMetrics metrics = new();
ResponseCache cache = new(host.Services.GetRequiredService<IChatClient>(), metrics, "response");
int requests = await Workload.RunAsync(cache, total: 100);
Console.WriteLine($"{requests} requests: {cache.Hits} hits, {cache.Misses} misses, hit rate {cache.HitRate:P0}");
Console.WriteLine("The console exporter prints agent.cache.hits, .misses and .hit_rate below.\n");
await host.StopAsync();   // flushes the last export
return 0;

/// <summary>The three instruments, and the one bounded tag they share.</summary>
public sealed class CacheMetrics : IDisposable
{
    public const string MeterName = "Contoso.Assistant.Cache";
    public static readonly string[] CacheNames = ["response", "policy", "embedding"];

    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _hits, _misses;
    private readonly List<ResponseCache> _caches = [];

    public CacheMetrics()
    {
        _hits = _meter.CreateCounter<long>("agent.cache.hits", "{request}", "Requests served from the cache");
        _misses = _meter.CreateCounter<long>("agent.cache.misses", "{request}", "Requests sent to the model");
        _meter.CreateObservableGauge("agent.cache.hit_rate", ObserveRates, "1", "Hits / (hits + misses)");
    }

    public void Register(ResponseCache cache)
    {
        lock (_caches) _caches.Add(cache);
    }

    public void Hit(string cacheName) => _hits.Add(1, new KeyValuePair<string, object?>("cache.name", cacheName));

    public void Miss(string cacheName) => _misses.Add(1, new KeyValuePair<string, object?>("cache.name", cacheName));

    private IEnumerable<Measurement<double>> ObserveRates()
    {
        lock (_caches)
            return [.. _caches.Where(c => c.Hits + c.Misses > 0)
                .Select(c => new Measurement<double>(c.HitRate, new KeyValuePair<string, object?>("cache.name", c.Name)))];
    }

    public void Dispose() => _meter.Dispose();
}

/// <summary>A response cache keyed on the last user message, inside the agent pipeline.</summary>
public sealed class ResponseCache : DelegatingChatClient
{
    private readonly CacheMetrics _metrics;
    private readonly Dictionary<string, ChatResponse> _entries = new(StringComparer.OrdinalIgnoreCase);
    private int _hits, _misses;

    public ResponseCache(IChatClient inner, CacheMetrics metrics, string name) : base(inner)
    {
        if (!CacheMetrics.CacheNames.Contains(name))
            throw new ArgumentException($"cache.name must be one of {string.Join(", ", CacheMetrics.CacheNames)}", nameof(name));
        (_metrics, Name) = (metrics, name);
        metrics.Register(this);
    }

    public string Name { get; }
    public int Hits => _hits;
    public int Misses => _misses;
    public double HitRate => _hits + _misses == 0 ? 0 : (double)_hits / (_hits + _misses);

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        string key = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";
        lock (_entries)
        {
            if (_entries.TryGetValue(key, out ChatResponse? cached))
            {
                Interlocked.Increment(ref _hits);
                _metrics.Hit(Name);
                return cached;
            }
        }
        Interlocked.Increment(ref _misses);
        _metrics.Miss(Name);
        ChatResponse response = await base.GetResponseAsync(messages, options, cancellationToken);
        lock (_entries) _entries[key] = response;
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (ChatResponseUpdate update in response.ToChatResponseUpdates()) yield return update;
    }
}

/// <summary>100 requests over 10 distinct questions, one agent run each.</summary>
public static class Workload
{
    public static readonly string[] Questions =
    [
        "What is C#?", "Explain async/await in one sentence.", "What is dependency injection?",
        "Name 3 SOLID principles.", "What is the difference between struct and class?", "What is LINQ?",
        "What does the using statement do?", "What is a record type in C#?", "What is garbage collection?",
        "What is the purpose of NuGet?",
    ];

    public static async Task<int> RunAsync(IChatClient cache, int total)
    {
        ChatClientAgent agent = new(cache, instructions: "Answer in one sentence.", name: "CachedAgent");
        for (int i = 0; i < total; i++)
            await agent.RunAsync(Questions[i % Questions.Length], await agent.CreateSessionAsync());
        return total;
    }
}

/// <summary>The offline check: dotnet run -- --self-test.</summary>
internal static class SelfTest
{
    private static int s_passed, s_total;

    public static async Task<int> RunAsync()
    {
        Console.WriteLine("=== Metrics collector: self-test (offline) ===\n");

        // The first edition's instrument: a counter called hit_rate, incremented on hits only.
        PointCopy v1 = new();
        using (MeterProvider provider = Listen("V1.Cache", v1))
        using (Meter meter = new("V1.Cache"))
        {
            Counter<long> hitsOnly = meter.CreateCounter<long>("agent.cache.hit_rate");
            for (int i = 0; i < 100; i++)
                if (i >= Workload.Questions.Length) hitsOnly.Add(1, new KeyValuePair<string, object?>("cache.name", "response-cache"));
            provider.ForceFlush();
        }
        Expect($"DEFECT REPRODUCED: the first edition exports agent.cache.hit_rate = {v1.Points.Sum(p => p.Value)} " +
               "(a count of hits) and no miss count, so no rate can be computed",
            v1.Points.All(p => p.Name == "agent.cache.hit_rate") && v1.Points.Sum(p => p.Value) == 90);

        PointCopy copy = new();
        List<(string Name, Dictionary<string, string> Tags, double Value)> points;
        int requests;
        using (MeterProvider provider = Listen(CacheMetrics.MeterName, copy))
        using (CacheMetrics metrics = new())
        {
            ResponseCache response = new(new EchoModel(), metrics, "response");
            ResponseCache policy = new(new EchoModel(), metrics, "policy");
            requests = await Workload.RunAsync(response, 100);
            await Workload.RunAsync(policy, 20);
            provider.ForceFlush();
            points = [.. copy.Points];   // before disposal: the shutdown export has no gauge callback left
        }

        double Value(string name, string cache) =>
            points.Where(p => p.Name == name && p.Tags["cache.name"] == cache).Sum(p => p.Value);

        foreach (var p in points.OrderBy(p => p.Name).ThenBy(p => p.Tags["cache.name"]))
            Console.WriteLine($"    {p.Name,-20} cache.name={p.Tags["cache.name"],-9} {p.Value:0.##}");

        Expect($"{requests} requests on the response cache export hits + misses = 100 " +
               $"({Value("agent.cache.hits", "response")} + {Value("agent.cache.misses", "response")})",
            Value("agent.cache.hits", "response") + Value("agent.cache.misses", "response") == 100
            && Value("agent.cache.misses", "response") == Workload.Questions.Length);
        Expect($"agent.cache.hit_rate for the response cache is {Value("agent.cache.hit_rate", "response"):0.##}, " +
               "hits / (hits + misses)",
            Math.Abs(Value("agent.cache.hit_rate", "response") - 0.9) < 1e-9);
        Expect("the only tag on every point is cache.name",
            points.Count > 0 && points.All(p => p.Tags.Keys.SequenceEqual(["cache.name"])));
        Expect($"cache.name takes at most 3 values (here: {string.Join(", ", points.Select(p => p.Tags["cache.name"]).Distinct())})",
            points.Select(p => p.Tags["cache.name"]).Distinct().Count() <= 3);

        bool rejected = false;
        try { using CacheMetrics m = new(); _ = new ResponseCache(new EchoModel(), m, "conv-1234"); }
        catch (ArgumentException) { rejected = true; }
        Expect("a cache.name outside the list (an id, say) is refused at construction", rejected);

        Console.WriteLine($"\nRESULT {s_passed}/{s_total}");
        return s_passed == s_total ? 0 : 1;
    }

    /// <summary>The real MeterProvider, reading into memory instead of a backend.</summary>
    private static MeterProvider Listen(string meterName, PointCopy copy) =>
        Sdk.CreateMeterProviderBuilder()
            .AddMeter(meterName)
            .AddReader(new BaseExportingMetricReader(copy) { TemporalityPreference = MetricReaderTemporalityPreference.Cumulative })
            .Build()!;

    private static void Expect(string name, bool ok)
    {
        s_total++;
        if (ok) s_passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    }
}

/// <summary>An exporter that copies each point: name, tags, value.</summary>
internal sealed class PointCopy : BaseExporter<Metric>
{
    public List<(string Name, Dictionary<string, string> Tags, double Value)> Points { get; } = [];

    public override ExportResult Export(in Batch<Metric> batch)
    {
        Points.Clear();
        foreach (Metric metric in batch)
        {
            foreach (ref readonly MetricPoint point in metric.GetMetricPoints())
            {
                Dictionary<string, string> tags = [];
                foreach (KeyValuePair<string, object?> t in point.Tags) tags[t.Key] = t.Value?.ToString() ?? "";
                double value = metric.MetricType == MetricType.DoubleGauge ? point.GetGaugeLastValueDouble() : point.GetSumLong();
                Points.Add((metric.Name, tags, value));
            }
        }
        return ExportResult.Success;
    }
}

/// <summary>A model stand-in: answers at once, echoing the question.</summary>
internal sealed class EchoModel : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Answer to: " + messages.Last().Text)));

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
